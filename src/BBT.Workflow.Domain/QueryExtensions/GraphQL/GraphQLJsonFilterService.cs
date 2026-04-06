using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using BBT.Workflow.Security;

namespace BBT.Workflow.Definitions.GraphQL;

/// <summary>
/// PostgreSQL native JSONB filter service using GraphQL-style JSON filter syntax
/// Supports logical operators (AND, OR, NOT) and all comparison operators
/// </summary>
public static class GraphQLJsonFilterService
{
    /// <summary>
    /// Apply GraphQL-style JSON filters using PostgreSQL native JSONB operators
    /// </summary>
    /// <typeparam name="T">Entity type</typeparam>
    /// <param name="dbSet">Entity DbSet</param>
    /// <param name="filterJson">JSON filter string</param>
    /// <param name="jsonColumnName">Name of the JSON column</param>
    /// <param name="tableName">Name of the database table</param>
    /// <param name="schema">Database schema name</param>
    /// <param name="schemaValidator">Optional schema validator for security validation</param>
    /// <returns>Filtered queryable</returns>
    public static IQueryable<T> ApplyGraphQLFilter<T>(
        this DbSet<T> dbSet,
        string filterJson,
        string jsonColumnName = "Data",
        string tableName = "",
        string schema = "public",
        ISchemaValidator? schemaValidator = null) where T : class
    {
        if (string.IsNullOrWhiteSpace(filterJson))
            return dbSet;

        // Validate JSON length
        InputValidator.ValidateJsonLength(filterJson);

        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);
        if (filterNode == null || filterNode.NodeType == FilterNodeType.Empty)
            return dbSet;

        return ApplyGraphQLFilter(dbSet, filterNode, jsonColumnName, tableName, schema, schemaValidator);
    }

    /// <summary>
    /// Apply GraphQL filter node using PostgreSQL native JSONB operators
    /// </summary>
    /// <param name="orderByClause">Optional ORDER BY clause (e.g. from BuildOrderByClause). When null, defaults to s."CreatedAt" DESC.</param>
    public static IQueryable<T> ApplyGraphQLFilter<T>(
        this DbSet<T> dbSet,
        GraphQLFilterNode filterNode,
        string jsonColumnName = "Data",
        string tableName = "",
        string schema = "public",
        ISchemaValidator? schemaValidator = null,
        ILogger? logger = null,
        string? orderByClause = null) where T : class
    {
        // Validate schema and table names
        if (schemaValidator != null)
        {
            schema = schemaValidator.ValidateSchemaSync(schema);
            tableName = schemaValidator.ValidateTableName(tableName);
        }
        else
        {
            // Fallback validation without DB lookup
            schema = new SyncSchemaValidator().ValidateSchemaSync(schema);
            tableName = new SyncSchemaValidator().ValidateTableName(tableName);
        }

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var (jsonWhereClause, instanceWhereClause) = BuildSeparatedWhereClauses(
            filterNode, jsonColumnName, parameters, ref parameterIndex, logger);

        if (string.IsNullOrEmpty(jsonWhereClause) && string.IsNullOrEmpty(instanceWhereClause))
            return dbSet;

        // Get table name if not provided
        if (string.IsNullOrEmpty(tableName))
        {
            tableName = typeof(T).Name + "s";
        }

        // Build CTE-based SQL query with both JSON and Instance filters
        var rawSql = $@"
            WITH FilteredData AS (
                SELECT DISTINCT ON (""InstanceId"") 
                    ""Id"",
                    ""InstanceId"",
                    ""Data"",
                    ""EnteredAt"",
                    ""IsLatest""
                FROM ""{schema}"".""InstancesData""
                WHERE ""IsLatest"" = true{(string.IsNullOrEmpty(jsonWhereClause) ? "" : $" AND ({jsonWhereClause})")}
                ORDER BY ""InstanceId"", ""EnteredAt"" DESC
            )
            SELECT s.*
            FROM ""{schema}"".""Instances"" s
            JOIN FilteredData d ON s.""Id"" = d.""InstanceId""
            {(string.IsNullOrEmpty(instanceWhereClause) ? "" : $"WHERE {instanceWhereClause}")}
            ORDER BY {(string.IsNullOrWhiteSpace(orderByClause) ? "s.\"CreatedAt\" DESC" : orderByClause)}";

        return dbSet.FromSqlRaw(rawSql, parameters.ToArray()).AsNoTracking();
    }

    /// <summary>
    /// Builds ORDER BY clause SQL for instance list (instance columns and/or attributes JSON path).
    /// </summary>
    public static string? BuildOrderByClause(
        OrderByRequest? orderBy,
        string schema,
        string instanceAlias = "s",
        string dataTableName = "InstancesData")
    {
        if (orderBy == null)
            return null;
        var entries = orderBy.GetEntries();
        if (entries.Count == 0)
            return null;

        var parts = new List<string>();
        foreach (var (field, direction) in entries)
        {
            var dir = direction.Equals("desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            var trimmed = field.Trim();
            if (trimmed.StartsWith("attributes.", StringComparison.OrdinalIgnoreCase))
            {
                var jsonPath = trimmed.Substring("attributes.".Length).Trim();
                if (string.IsNullOrEmpty(jsonPath) || !IsSafeJsonPath(jsonPath))
                    continue;
                var accessor = BuildJsonTextAccessorForOrderBy(jsonPath);
                parts.Add($"(SELECT {accessor} FROM \"{schema}\".\"{dataTableName}\" _d WHERE _d.\"InstanceId\" = {instanceAlias}.\"Id\" AND _d.\"IsLatest\" = true LIMIT 1) {dir}");
            }
            else if (InstanceFieldDiscriminator.IsInstanceColumn(trimmed))
            {
                try
                {
                    var columnName = InstanceFieldDiscriminator.GetInstanceColumnName(trimmed);
                    parts.Add($"{instanceAlias}.\"{columnName}\" {dir}");
                }
                catch (ArgumentException) { }
            }
        }
        if (parts.Count == 0)
            return null;
        return string.Join(", ", parts);
    }

    private static bool IsSafeJsonPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var segments = path.Split('.');
        var safeSegment = new Regex("^[a-zA-Z0-9_]+$");
        return segments.All(seg => safeSegment.IsMatch(seg.Trim()));
    }

    private static string BuildJsonTextAccessorForOrderBy(string field)
    {
        if (field.Contains('.'))
        {
            var parts = field.Split('.');
            var arrayElements = string.Join(",", parts.Select(p =>
                "'" + InputValidator.EscapePostgresSingleQuotedString(p.Trim()) + "'"));
            return "\"Data\" #>> ARRAY[" + arrayElements + "]";
        }
        return "\"Data\" ->> '" + InputValidator.EscapePostgresSingleQuotedString(field.Trim()) + "'";
    }

    /// <summary>
    /// Build separated WHERE clauses from GraphQL filter node (JSON and Instance filters)
    /// </summary>
    private static (string jsonWhereClause, string instanceWhereClause) BuildSeparatedWhereClauses(
        GraphQLFilterNode node,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex,
        ILogger? logger = null)
    {
        var jsonClauses = new List<string>();
        var instanceClauses = new List<string>();

        BuildSeparatedClauses(node, jsonColumnName, parameters, jsonClauses, instanceClauses, ref parameterIndex, logger);

        var jsonWhereClause = jsonClauses.Count > 0 ? string.Join(" AND ", jsonClauses) : string.Empty;
        var instanceWhereClause = instanceClauses.Count > 0 ? string.Join(" AND ", instanceClauses) : string.Empty;

        return (jsonWhereClause, instanceWhereClause);
    }

    /// <summary>
    /// Builds JSON and Instance WHERE fragments for raw SQL (e.g. aggregations with optional <c>Instances</c> join).
    /// Instance fragments use alias <c>s</c>; JSON fragments reference the JSON column on <c>InstancesData</c>.
    /// </summary>
    public static (string jsonWhereClause, string instanceWhereClause) BuildSeparatedWhereClausesForSql(
        GraphQLFilterNode? filterNode,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex,
        ILogger? logger = null)
    {
        if (filterNode == null || filterNode.NodeType == FilterNodeType.Empty)
            return (string.Empty, string.Empty);

        return BuildSeparatedWhereClauses(filterNode, jsonColumnName, parameters, ref parameterIndex, logger);
    }

    /// <summary>
    /// Build WHERE clauses recursively, separating Instance and JSON conditions
    /// </summary>
    private static void BuildSeparatedClauses(
        GraphQLFilterNode node,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        List<string> jsonClauses,
        List<string> instanceClauses,
        ref int parameterIndex,
        ILogger? logger = null)
    {
        switch (node.NodeType)
        {
            case FilterNodeType.And:
                foreach (var childNode in node.And!)
                {
                    BuildSeparatedClauses(childNode, jsonColumnName, parameters, jsonClauses, instanceClauses, ref parameterIndex, logger);
                }
                break;

            case FilterNodeType.Or:
                // For OR, we need to build complete sub-clauses and combine them
                var orJsonClauses = new List<string>();
                var orInstanceClauses = new List<string>();
                foreach (var childNode in node.Or!)
                {
                    BuildSeparatedClauses(childNode, jsonColumnName, parameters, orJsonClauses, orInstanceClauses, ref parameterIndex, logger);
                }
                if (orJsonClauses.Count > 0)
                    jsonClauses.Add($"({string.Join(" OR ", orJsonClauses)})");
                if (orInstanceClauses.Count > 0)
                    instanceClauses.Add($"({string.Join(" OR ", orInstanceClauses)})");
                break;

            case FilterNodeType.Not:
                var notJsonClauses = new List<string>();
                var notInstanceClauses = new List<string>();
                BuildSeparatedClauses(node.Not!, jsonColumnName, parameters, notJsonClauses, notInstanceClauses, ref parameterIndex, logger);
                if (notJsonClauses.Count > 0)
                    jsonClauses.Add($"NOT ({string.Join(" AND ", notJsonClauses)})");
                if (notInstanceClauses.Count > 0)
                    instanceClauses.Add($"NOT ({string.Join(" AND ", notInstanceClauses)})");
                break;

            case FilterNodeType.Condition:
                var (jsonConditions, instanceConditions) = BuildSeparatedConditionClauses(
                    node.Attributes!, jsonColumnName, parameters, ref parameterIndex, logger);
                jsonClauses.AddRange(jsonConditions);
                instanceClauses.AddRange(instanceConditions);
                break;
            default:
                // Unknown node type, ignore
                break;
        }
    }

    /// <summary>
    /// Build WHERE clause from GraphQL filter node with full logical operator support
    /// </summary>
    public static string BuildWhereClause(
        GraphQLFilterNode node,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        return node.NodeType switch
        {
            FilterNodeType.And => BuildAndClause(node.And!, jsonColumnName, parameters, ref parameterIndex),
            FilterNodeType.Or => BuildOrClause(node.Or!, jsonColumnName, parameters, ref parameterIndex),
            FilterNodeType.Not => BuildNotClause(node.Not!, jsonColumnName, parameters, ref parameterIndex),
            FilterNodeType.Condition => BuildConditionClause(node.Attributes!, jsonColumnName, parameters, ref parameterIndex),
            _ => string.Empty
        };
    }

    private static string BuildAndClause(
        List<GraphQLFilterNode> nodes,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        var clauses = new List<string>();

        foreach (var node in nodes)
        {
            var clause = BuildWhereClause(node, jsonColumnName, parameters, ref parameterIndex);
            if (!string.IsNullOrEmpty(clause))
            {
                clauses.Add($"({clause})");
            }
        }

        return clauses.Count > 0 
            ? string.Join(" AND ", clauses) 
            : string.Empty;
    }

    private static string BuildOrClause(
        List<GraphQLFilterNode> nodes,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        var clauses = new List<string>();

        foreach (var node in nodes)
        {
            var clause = BuildWhereClause(node, jsonColumnName, parameters, ref parameterIndex);
            if (!string.IsNullOrEmpty(clause))
            {
                clauses.Add($"({clause})");
            }
        }

        return clauses.Count > 0 
            ? $"({string.Join(" OR ", clauses)})" 
            : string.Empty;
    }

    private static string BuildNotClause(
        GraphQLFilterNode node,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        var clause = BuildWhereClause(node, jsonColumnName, parameters, ref parameterIndex);
        return !string.IsNullOrEmpty(clause) 
            ? $"NOT ({clause})" 
            : string.Empty;
    }

    private static string BuildConditionClause(
        Dictionary<string, FieldCondition> attributes,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        var conditions = new List<string>();

        foreach (var (fieldName, fieldCondition) in attributes)
        {
            var fieldConditions = BuildFieldConditions(fieldName, fieldCondition, jsonColumnName, parameters, ref parameterIndex);
            conditions.AddRange(fieldConditions);
        }

        return conditions.Count > 0 
            ? string.Join(" AND ", conditions) 
            : string.Empty;
    }

    /// <summary>
    /// Build separated condition clauses for Instance and JSON fields
    /// </summary>
    private static (List<string> jsonConditions, List<string> instanceConditions) BuildSeparatedConditionClauses(
        Dictionary<string, FieldCondition> attributes,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex,
        ILogger? logger = null)
    {
        var jsonConditions = new List<string>();
        var instanceConditions = new List<string>();

        foreach (var (fieldName, fieldCondition) in attributes)
        {
            // Check if this is an Instance column or JSON field
            if (InstanceFieldDiscriminator.IsInstanceColumn(fieldName))
            {
                // Build Instance column conditions
                var conditions = BuildInstanceFieldConditions(fieldName, fieldCondition, parameters, ref parameterIndex, logger);
                instanceConditions.AddRange(conditions);
            }
            else
            {
                // Build JSON field conditions
                var conditions = BuildFieldConditions(fieldName, fieldCondition, jsonColumnName, parameters, ref parameterIndex);
                jsonConditions.AddRange(conditions);
            }
        }

        return (jsonConditions, instanceConditions);
    }

    /// <summary>
    /// Build conditions for Instance table columns
    /// </summary>
    private static List<string> BuildInstanceFieldConditions(
        string fieldName,
        FieldCondition condition,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex,
        ILogger? logger = null)
    {
        var conditions = new List<string>();
        var columnName = InstanceFieldDiscriminator.GetInstanceColumnName(fieldName);

        // Process each operator in the condition
        foreach (var (op, value) in condition.GetOperators())
        {
            try
            {
                var stringValue = ConvertToString(value);
                var (conditionSql, conditionParams) = InstanceColumnConditionBuilder.BuildCondition(
                    columnName, op, stringValue, ref parameterIndex);
                
                if (!string.IsNullOrEmpty(conditionSql))
                {
                    conditions.Add(conditionSql);
                    parameters.AddRange(conditionParams);
                }
            }
            catch (ArgumentException ex)
            {
                logger?.LogWarning(ex, "Error building Instance condition for field: {FieldName}", fieldName);
            }
            catch (FormatException ex)
            {
                logger?.LogWarning(ex, "Error building Instance condition for field: {FieldName}", fieldName);
            }
        }

        return conditions;
    }

    private static List<string> BuildFieldConditions(
        string fieldName,
        FieldCondition condition,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        var sanitizedField = SanitizeFieldName(fieldName);
        var conditions = new List<string>();

        // Process each operator in the condition
        foreach (var (op, value) in condition.GetOperators())
        {
            var conditionSql = BuildOperatorCondition(
                sanitizedField, op, value, jsonColumnName, parameters, ref parameterIndex);
            
            if (!string.IsNullOrEmpty(conditionSql))
            {
                conditions.Add(conditionSql);
            }
        }

        // Process nested conditions (for deeply nested field access)
        if (condition.NestedConditions != null)
        {
            foreach (var (nestedField, nestedValue) in condition.NestedConditions)
            {
                var nestedConditions = ProcessNestedCondition(
                    $"{sanitizedField}.{nestedField}", nestedValue, jsonColumnName, parameters, ref parameterIndex);
                conditions.AddRange(nestedConditions);
            }
        }

        return conditions;
    }

    private static List<string> ProcessNestedCondition(
        string fieldPath,
        object value,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        var conditions = new List<string>();

        if (value is System.Text.Json.JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in jsonElement.EnumerateObject())
                {
                    if (IsOperator(prop.Name))
                    {
                        var opValue = ConvertJsonElement(prop.Value);
                        var conditionSql = BuildOperatorCondition(
                            fieldPath, prop.Name.ToLowerInvariant(), opValue, 
                            jsonColumnName, parameters, ref parameterIndex);
                        
                        if (!string.IsNullOrEmpty(conditionSql))
                        {
                            conditions.Add(conditionSql);
                        }
                    }
                    else
                    {
                        // More nesting
                        var nestedConditions = ProcessNestedCondition(
                            $"{fieldPath}.{prop.Name}", prop.Value, 
                            jsonColumnName, parameters, ref parameterIndex);
                        conditions.AddRange(nestedConditions);
                    }
                }
            }
        }

        return conditions;
    }

    private static object? ConvertJsonElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            System.Text.Json.JsonValueKind.Number when element.TryGetDecimal(out var d) => d,
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToArray(),
            _ => element.GetRawText()
        };
    }

    private static bool IsOperator(string name)
    {
        var lowerName = name.ToLowerInvariant();
        return lowerName switch
        {
            "eq" or "ne" or "gt" or "ge" or "lt" or "le" or
            "between" or "like" or "match" or "startswith" or "endswith" or
            "in" or "nin" or "isnull" => true,
            _ => false
        };
    }

    private static string BuildOperatorCondition(
        string field,
        string operatorType,
        object? value,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        return operatorType.ToLowerInvariant() switch
        {
            "eq" => BuildEqualsCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "ne" => BuildNotEqualsCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "gt" => BuildNumericCondition(field, value, ">", jsonColumnName, parameters, ref parameterIndex),
            "ge" => BuildNumericCondition(field, value, ">=", jsonColumnName, parameters, ref parameterIndex),
            "lt" => BuildNumericCondition(field, value, "<", jsonColumnName, parameters, ref parameterIndex),
            "le" => BuildNumericCondition(field, value, "<=", jsonColumnName, parameters, ref parameterIndex),
            "between" => BuildBetweenCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "like" or "match" => BuildLikeCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "startswith" => BuildStartsWithCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "endswith" => BuildEndsWithCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "in" => BuildInCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "nin" => BuildNotInCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "isnull" => BuildIsNullCondition(field, value, jsonColumnName),
            _ => throw new ArgumentException($"Unsupported operator: {operatorType}")
        };
    }

    private static string SanitizeFieldName(string field)
    {
        // Validate field name with comprehensive checks
        InputValidator.ValidateFieldName(field);
        
        // Must start with a letter
        if (!char.IsLetter(field[0]))
        {
            throw new ArgumentException($"Invalid field name: {field}. Must start with a letter.");
        }
        
        // Only allow alphanumeric characters, dots, and underscores
        var regex = new System.Text.RegularExpressions.Regex(
            @"^[a-zA-Z][a-zA-Z0-9._]*$",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(100));
            
        try
        {
            if (!regex.IsMatch(field))
            {
                throw new ArgumentException($"Invalid field name: {field}. Only alphanumeric, dots, and underscores allowed.");
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw new ArgumentException($"Field name validation timeout: {field}");
        }
        
        return field;
    }

    private static bool IsNestedPath(string field) => field.Contains('.');

    private static string BuildJsonTextAccessor(string field, string jsonColumnName)
    {
        InputValidator.ValidateSqlJsonColumnIdentifier(jsonColumnName);
        if (IsNestedPath(field))
        {
            var parts = field.Split('.');
            var arrayElements = string.Join(",", parts.Select(p =>
                $"'{InputValidator.EscapePostgresSingleQuotedString(p)}'"));
            return $"(\"{jsonColumnName}\" #>> ARRAY[{arrayElements}])";
        }

        return $"(\"{jsonColumnName}\" ->> '{InputValidator.EscapePostgresSingleQuotedString(field)}')";
    }

    private static string BuildNestedJsonContainmentPattern(string field, object? value, bool isNumeric, bool isBoolean)
    {
        var parts = field.Split('.');
        var stringValue = ConvertToString(value);
        
        // Build the innermost value as proper object
        object innerValue;
        if (isBoolean && bool.TryParse(stringValue, out var boolVal))
        {
            innerValue = boolVal;
        }
        else if (isNumeric && decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var numVal))
        {
            innerValue = numVal;
        }
        else
        {
            innerValue = stringValue; // JsonSerializer will properly escape strings
        }
        
        // Build nested object from inside out using Dictionary for proper JSON serialization
        object currentLevel = new Dictionary<string, object> { [parts[^1]] = innerValue };
        
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            currentLevel = new Dictionary<string, object> { [parts[i]] = currentLevel };
        }
        
        // Use JsonSerializer for proper escaping and formatting
        return System.Text.Json.JsonSerializer.Serialize(currentLevel, new System.Text.Json.JsonSerializerOptions 
        { 
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string ConvertToString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b.ToString().ToLowerInvariant(),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string BuildEqualsCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var conditions = new List<string>();
        var isNested = IsNestedPath(field);

        // String comparison
        var stringIndex = parameterIndex++;
        var stringJsonPattern = isNested
            ? BuildNestedJsonContainmentPattern(field, value, isNumeric: false, isBoolean: false)
            : $"{{\"{field}\":\"{stringValue}\"}}";
        parameters.Add(new NpgsqlParameter { Value = stringJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
        conditions.Add($"\"{jsonColumnName}\" @> {{{stringIndex}}}");

        // Numeric comparison
        if (decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            var numIndex = parameterIndex++;
            var numJsonPattern = isNested
                ? BuildNestedJsonContainmentPattern(field, value, isNumeric: true, isBoolean: false)
                : $"{{\"{field}\":{decimal.Parse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture)}}}";
            parameters.Add(new NpgsqlParameter { Value = numJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
            conditions.Add($"\"{jsonColumnName}\" @> {{{numIndex}}}");
        }

        // Boolean comparison
        if (bool.TryParse(stringValue, out _))
        {
            var boolIndex = parameterIndex++;
            var boolJsonPattern = isNested
                ? BuildNestedJsonContainmentPattern(field, value, isNumeric: false, isBoolean: true)
                : $"{{\"{field}\":{bool.Parse(stringValue).ToString().ToLowerInvariant()}}}";
            parameters.Add(new NpgsqlParameter { Value = boolJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
            conditions.Add($"\"{jsonColumnName}\" @> {{{boolIndex}}}");
        }

        return $"({string.Join(" OR ", conditions)})";
    }

    private static string BuildNotEqualsCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var conditions = new List<string>();
        var isNested = IsNestedPath(field);

        // String comparison
        var stringIndex = parameterIndex++;
        var stringJsonPattern = isNested
            ? BuildNestedJsonContainmentPattern(field, value, isNumeric: false, isBoolean: false)
            : $"{{\"{field}\":\"{stringValue}\"}}";
        parameters.Add(new NpgsqlParameter { Value = stringJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
        conditions.Add($"NOT (\"{jsonColumnName}\" @> {{{stringIndex}}})");

        // Numeric comparison
        if (decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            var numIndex = parameterIndex++;
            var numJsonPattern = isNested
                ? BuildNestedJsonContainmentPattern(field, value, isNumeric: true, isBoolean: false)
                : $"{{\"{field}\":{decimal.Parse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture)}}}";
            parameters.Add(new NpgsqlParameter { Value = numJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
            conditions.Add($"NOT (\"{jsonColumnName}\" @> {{{numIndex}}})");
        }

        // Boolean comparison
        if (bool.TryParse(stringValue, out _))
        {
            var boolIndex = parameterIndex++;
            var boolJsonPattern = isNested
                ? BuildNestedJsonContainmentPattern(field, value, isNumeric: false, isBoolean: true)
                : $"{{\"{field}\":{bool.Parse(stringValue).ToString().ToLowerInvariant()}}}";
            parameters.Add(new NpgsqlParameter { Value = boolJsonPattern, NpgsqlDbType = NpgsqlDbType.Jsonb });
            conditions.Add($"NOT (\"{jsonColumnName}\" @> {{{boolIndex}}})");
        }

        return $"({string.Join(" AND ", conditions)})";
    }

    private static string BuildNumericCondition(
        string field, object? value, string sqlOperator, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        
        if (!decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var numValue))
        {
            throw new ArgumentException($"Value '{stringValue}' is not numeric for comparison operator '{sqlOperator}'");
        }

        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = numValue });

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor}::numeric {sqlOperator} {{{paramIndex}}}";
    }

    private static string BuildBetweenCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        object[] values;
        if (value is object[] arr)
        {
            values = arr;
        }
        else
        {
            var stringValue = ConvertToString(value);
            values = stringValue.Split(',').Select(v => (object)v.Trim()).ToArray();
        }

        if (values.Length != 2)
            throw new ArgumentException($"Invalid between format. Expected 2 values, got {values.Length}");

        var minString = ConvertToString(values[0]);
        var maxString = ConvertToString(values[1]);

        if (!decimal.TryParse(minString, NumberStyles.Number, CultureInfo.InvariantCulture, out var minNum) ||
            !decimal.TryParse(maxString, NumberStyles.Number, CultureInfo.InvariantCulture, out var maxNum))
        {
            throw new ArgumentException($"Between values must be numeric: '{minString}', '{maxString}'");
        }

        var minIndex = parameterIndex++;
        var maxIndex = parameterIndex++;

        parameters.Add(new NpgsqlParameter { Value = minNum });
        parameters.Add(new NpgsqlParameter { Value = maxNum });

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor}::numeric BETWEEN {{{minIndex}}} AND {{{maxIndex}}}";
    }

    private static string BuildLikeCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = $"%{stringValue}%" });

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} ILIKE {{{paramIndex}}}";
    }

    private static string BuildStartsWithCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = $"{stringValue}%" });

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} ILIKE {{{paramIndex}}}";
    }

    private static string BuildEndsWithCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);
        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = $"%{stringValue}" });

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} ILIKE {{{paramIndex}}}";
    }

    private static string BuildInCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        object[] values;
        if (value is object[] arr)
        {
            values = arr;
        }
        else
        {
            var stringValue = ConvertToString(value);
            values = stringValue.Split(',').Select(v => (object)v.Trim()).ToArray();
        }

        var paramPlaceholders = new List<string>();

        foreach (var val in values)
        {
            var paramIndex = parameterIndex++;
            parameters.Add(new NpgsqlParameter { Value = ConvertToString(val) });
            paramPlaceholders.Add($"{{{paramIndex}}}");
        }

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} IN ({string.Join(", ", paramPlaceholders)})";
    }

    private static string BuildNotInCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        object[] values;
        if (value is object[] arr)
        {
            values = arr;
        }
        else
        {
            var stringValue = ConvertToString(value);
            values = stringValue.Split(',').Select(v => (object)v.Trim()).ToArray();
        }

        var paramPlaceholders = new List<string>();

        foreach (var val in values)
        {
            var paramIndex = parameterIndex++;
            parameters.Add(new NpgsqlParameter { Value = ConvertToString(val) });
            paramPlaceholders.Add($"{{{paramIndex}}}");
        }

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return $"{accessor} IS NOT NULL AND {accessor} NOT IN ({string.Join(", ", paramPlaceholders)})";
    }

    private static string BuildIsNullCondition(
        string field, object? value, string jsonColumnName)
    {
        var isNull = value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => true
        };

        var accessor = BuildJsonTextAccessor(field, jsonColumnName);
        return isNull 
            ? $"{accessor} IS NULL" 
            : $"{accessor} IS NOT NULL";
    }
}


