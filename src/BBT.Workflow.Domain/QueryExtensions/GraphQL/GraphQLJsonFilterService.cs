using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
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
        ISchemaValidator? schemaValidator = null,
        SchemaFilterContext? schemaContext = null) where T : class
    {
        if (string.IsNullOrWhiteSpace(filterJson))
            return dbSet;

        // Validate JSON length
        InputValidator.ValidateJsonLength(filterJson);

        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);
        if (filterNode == null || filterNode.NodeType == FilterNodeType.Empty)
            return dbSet;

        return ApplyGraphQLFilter(dbSet, filterNode, jsonColumnName, tableName, schema, schemaValidator, schemaContext: schemaContext);
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
        string? orderByClause = null,
        SchemaFilterContext? schemaContext = null) where T : class
    {
        if (schemaValidator != null)
        {
            schema = schemaValidator.ValidateSchemaSync(schema);
            tableName = schemaValidator.ValidateTableName(tableName);
        }
        else
        {
            schema = new SyncSchemaValidator().ValidateSchemaSync(schema);
            tableName = new SyncSchemaValidator().ValidateTableName(tableName);
        }

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var (jsonWhereClause, instanceWhereClause) = BuildSeparatedWhereClauses(
            filterNode, jsonColumnName, parameters, ref parameterIndex, logger, schemaContext);

        if (string.IsNullOrEmpty(jsonWhereClause) && string.IsNullOrEmpty(instanceWhereClause))
            return dbSet;

        if (string.IsNullOrEmpty(tableName))
        {
            tableName = typeof(T).Name + "s";
        }

        var hasJsonFilter = !string.IsNullOrEmpty(jsonWhereClause);
        var hasInstanceFilter = !string.IsNullOrEmpty(instanceWhereClause);
        var orderBy = string.IsNullOrWhiteSpace(orderByClause) ? "s.\"CreatedAt\" DESC" : orderByClause;

        string rawSql;
        if (hasJsonFilter && !hasInstanceFilter)
        {
            rawSql = $@"
            SELECT s.*
            FROM ""{schema}"".""Instances"" s
            WHERE s.""Id"" IN (
                SELECT ""InstanceId""
                FROM ""{schema}"".""InstancesData""
                WHERE ""IsLatest"" = true AND ({jsonWhereClause})
            )
            ORDER BY {orderBy}";
        }
        else if (hasJsonFilter && hasInstanceFilter)
        {
            rawSql = $@"
            SELECT s.*
            FROM ""{schema}"".""Instances"" s
            WHERE s.""Id"" IN (
                SELECT ""InstanceId""
                FROM ""{schema}"".""InstancesData""
                WHERE ""IsLatest"" = true AND ({jsonWhereClause})
            )
            AND {instanceWhereClause}
            ORDER BY {orderBy}";
        }
        else
        {
            rawSql = $@"
            SELECT s.*
            FROM ""{schema}"".""Instances"" s
            WHERE {instanceWhereClause}
            ORDER BY {orderBy}";
        }

        return dbSet.FromSqlRaw(rawSql, parameters.ToArray()).AsNoTracking();
    }

    /// <summary>
    /// Builds ORDER BY clause SQL for instance list (instance columns and/or attributes JSON path).
    /// </summary>
    public static string? BuildOrderByClause(
        OrderByRequest? orderBy,
        string schema,
        string instanceAlias = "s",
        string dataTableName = "InstancesData",
        SchemaFilterContext? schemaContext = null)
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

                // When schema context is available, skip non-sortable fields
                if (schemaContext != null && !schemaContext.IsFieldSortable(jsonPath))
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
        ILogger? logger = null,
        SchemaFilterContext? schemaContext = null)
    {
        var jsonClauses = new List<string>();
        var instanceClauses = new List<string>();

        BuildSeparatedClauses(node, jsonColumnName, parameters, jsonClauses, instanceClauses, ref parameterIndex, logger, schemaContext);

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
        ILogger? logger = null,
        SchemaFilterContext? schemaContext = null)
    {
        if (filterNode == null || filterNode.NodeType == FilterNodeType.Empty)
            return (string.Empty, string.Empty);

        return BuildSeparatedWhereClauses(filterNode, jsonColumnName, parameters, ref parameterIndex, logger, schemaContext);
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
        ILogger? logger = null,
        SchemaFilterContext? schemaContext = null)
    {
        switch (node.NodeType)
        {
            case FilterNodeType.And:
                foreach (var childNode in node.And!)
                {
                    BuildSeparatedClauses(childNode, jsonColumnName, parameters, jsonClauses, instanceClauses, ref parameterIndex, logger, schemaContext);
                }
                break;

            case FilterNodeType.Or:
                var orJsonClauses = new List<string>();
                var orInstanceClauses = new List<string>();
                foreach (var childNode in node.Or!)
                {
                    BuildSeparatedClauses(childNode, jsonColumnName, parameters, orJsonClauses, orInstanceClauses, ref parameterIndex, logger, schemaContext);
                }
                if (orJsonClauses.Count > 0)
                    jsonClauses.Add($"({string.Join(" OR ", orJsonClauses)})");
                if (orInstanceClauses.Count > 0)
                    instanceClauses.Add($"({string.Join(" OR ", orInstanceClauses)})");
                break;

            case FilterNodeType.Not:
                var notJsonClauses = new List<string>();
                var notInstanceClauses = new List<string>();
                BuildSeparatedClauses(node.Not!, jsonColumnName, parameters, notJsonClauses, notInstanceClauses, ref parameterIndex, logger, schemaContext);
                if (notJsonClauses.Count > 0)
                    jsonClauses.Add($"NOT ({string.Join(" AND ", notJsonClauses)})");
                if (notInstanceClauses.Count > 0)
                    instanceClauses.Add($"NOT ({string.Join(" AND ", notInstanceClauses)})");
                break;

            case FilterNodeType.Condition:
                var (jsonConditions, instanceConditions) = BuildSeparatedConditionClauses(
                    node.Attributes!, jsonColumnName, parameters, ref parameterIndex, logger, schemaContext);
                jsonClauses.AddRange(jsonConditions);
                instanceClauses.AddRange(instanceConditions);
                break;
            default:
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
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        return node.NodeType switch
        {
            FilterNodeType.And => BuildAndClause(node.And!, jsonColumnName, parameters, ref parameterIndex, schemaContext),
            FilterNodeType.Or => BuildOrClause(node.Or!, jsonColumnName, parameters, ref parameterIndex, schemaContext),
            FilterNodeType.Not => BuildNotClause(node.Not!, jsonColumnName, parameters, ref parameterIndex, schemaContext),
            FilterNodeType.Condition => BuildConditionClause(node.Attributes!, jsonColumnName, parameters, ref parameterIndex, schemaContext),
            _ => string.Empty
        };
    }

    private static string BuildAndClause(
        List<GraphQLFilterNode> nodes,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        var clauses = new List<string>();

        foreach (var node in nodes)
        {
            var clause = BuildWhereClause(node, jsonColumnName, parameters, ref parameterIndex, schemaContext);
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
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        var clauses = new List<string>();

        foreach (var node in nodes)
        {
            var clause = BuildWhereClause(node, jsonColumnName, parameters, ref parameterIndex, schemaContext);
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
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        var clause = BuildWhereClause(node, jsonColumnName, parameters, ref parameterIndex, schemaContext);
        return !string.IsNullOrEmpty(clause) 
            ? $"NOT ({clause})" 
            : string.Empty;
    }

    private static string BuildConditionClause(
        Dictionary<string, FieldCondition> attributes,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        var conditions = new List<string>();

        foreach (var (fieldName, fieldCondition) in attributes)
        {
            var fieldConditions = BuildFieldConditions(fieldName, fieldCondition, jsonColumnName, parameters, ref parameterIndex, schemaContext);
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
        ILogger? logger = null,
        SchemaFilterContext? schemaContext = null)
    {
        var jsonConditions = new List<string>();
        var instanceConditions = new List<string>();

        foreach (var (fieldName, fieldCondition) in attributes)
        {
            if (InstanceFieldDiscriminator.IsInstanceColumn(fieldName))
            {
                var conditions = BuildInstanceFieldConditions(fieldName, fieldCondition, parameters, ref parameterIndex, logger);
                instanceConditions.AddRange(conditions);
            }
            else
            {
                var conditions = BuildFieldConditions(fieldName, fieldCondition, jsonColumnName, parameters, ref parameterIndex, schemaContext);
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
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        var sanitizedField = SanitizeFieldName(fieldName);
        var conditions = new List<string>();

        if (schemaContext != null && !schemaContext.IsFieldFilterable(sanitizedField))
            throw new SchemaFilterValidationException($"Field '{sanitizedField}' is not filterable.");

        var operatorList = condition.GetOperators().ToList();
        if (operatorList.Exists(static o => o.Operator == "includes"))
        {
            if (operatorList.Count > 1)
                throw new SchemaFilterValidationException(
                    $"Field '{sanitizedField}': the includes operator cannot be combined with other operators on the same field.");
            if (condition.NestedConditions is { Count: > 0 })
                throw new SchemaFilterValidationException(
                    $"Field '{sanitizedField}': the includes operator cannot be combined with nested field conditions on the same field.");
        }

        foreach (var (op, value) in operatorList)
        {
            if (schemaContext != null && !schemaContext.IsOperatorAllowed(sanitizedField, op))
                throw new SchemaFilterValidationException($"Operator '{op}' is not allowed for field '{sanitizedField}'.");

            var conditionSql = BuildOperatorCondition(
                sanitizedField, op, value, jsonColumnName, parameters, ref parameterIndex, schemaContext);
            
            if (!string.IsNullOrEmpty(conditionSql))
            {
                conditions.Add(conditionSql);
            }
        }

        if (condition.NestedConditions != null)
        {
            foreach (var (nestedField, nestedValue) in condition.NestedConditions)
            {
                var nestedConditions = ProcessNestedCondition(
                    $"{sanitizedField}.{nestedField}", nestedValue, jsonColumnName, parameters, ref parameterIndex, schemaContext);
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
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        var conditions = new List<string>();

        if (value is System.Text.Json.JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var props = jsonElement.EnumerateObject().ToList();
                var operatorProps = props.Where(static p => IsOperator(p.Name)).ToList();
                if (operatorProps.Count > 1 &&
                    operatorProps.Exists(static p => p.Name.Equals("includes", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SchemaFilterValidationException(
                        $"Field '{fieldPath}': the includes operator cannot be combined with other operators on the same field.");
                }

                foreach (var prop in props)
                {
                    if (IsOperator(prop.Name))
                    {
                        var opName = prop.Name.ToLowerInvariant();
                        object? opValue = opName == "includes"
                            ? prop.Value
                            : ConvertJsonElement(prop.Value);
                        var conditionSql = BuildOperatorCondition(
                            fieldPath, opName, opValue, 
                            jsonColumnName, parameters, ref parameterIndex, schemaContext);
                        
                        if (!string.IsNullOrEmpty(conditionSql))
                        {
                            conditions.Add(conditionSql);
                        }
                    }
                    else
                    {
                        var nestedConditions = ProcessNestedCondition(
                            $"{fieldPath}.{prop.Name}", prop.Value, 
                            jsonColumnName, parameters, ref parameterIndex, schemaContext);
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
            "in" or "nin" or "isnull" or "includes" => true,
            _ => false
        };
    }

    private static string BuildOperatorCondition(
        string field,
        string operatorType,
        object? value,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        return operatorType.ToLowerInvariant() switch
        {
            "eq" => BuildEqualsCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "ne" => BuildNotEqualsCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "gt" => BuildComparisonCondition(field, value, ">", jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "ge" => BuildComparisonCondition(field, value, ">=", jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "lt" => BuildComparisonCondition(field, value, "<", jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "le" => BuildComparisonCondition(field, value, "<=", jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "between" => BuildBetweenCondition(field, value, jsonColumnName, parameters, ref parameterIndex, schemaContext),
            "like" or "match" => BuildLikeCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "startswith" => BuildStartsWithCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "endswith" => BuildEndsWithCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "in" => BuildInCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "nin" => BuildNotInCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            "isnull" => BuildIsNullCondition(field, value, jsonColumnName),
            "includes" => BuildIncludesCondition(field, value, jsonColumnName, parameters, ref parameterIndex),
            _ => throw new ArgumentException($"Unsupported operator: {operatorType}")
        };
    }

    /// <summary>
    /// Builds <c>jsonb @&gt;</c> for "array at field path contains an element matching partial object".
    /// </summary>
    private static string BuildIncludesCondition(
        string field,
        object? value,
        string jsonColumnName,
        List<NpgsqlParameter> parameters,
        ref int parameterIndex)
    {
        InputValidator.ValidateSqlJsonColumnIdentifier(jsonColumnName);

        if (value is not JsonElement partial || partial.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("includes value must be a JSON object.");

        InputValidator.ValidateIncludesObject(partial);

        var parts = field.Split('.');
        if (parts.Length == 0)
            throw new ArgumentException("Field path cannot be empty.");

        JsonNode inner = JsonNode.Parse(partial.GetRawText())!;
        JsonNode current = new JsonArray(inner);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var wrap = new JsonObject { [parts[i]] = current };
            current = wrap;
        }

        var jsonText = current.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        if (jsonText.Length > InputValidator.MaxFilterLength)
            throw new ArgumentException($"includes pattern exceeds maximum length ({jsonText.Length} characters).");

        var idx = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = jsonText, NpgsqlDbType = NpgsqlDbType.Jsonb });
        return $"\"{jsonColumnName}\" @> {{{idx}}}";
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

    /// <summary>
    /// Resolves the effective schema type for a field. When schema context is available,
    /// uses the declared type; otherwise falls back to "number" for backward compatibility.
    /// </summary>
    private static string ResolveFieldType(string field, SchemaFilterContext? schemaContext)
    {
        if (schemaContext == null)
            return "number";

        var metadata = schemaContext.GetFieldMetadata(field);
        return metadata?.Type ?? "number";
    }

    private static string BuildComparisonCondition(
        string field, object? value, string sqlOperator, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        var fieldType = ResolveFieldType(field, schemaContext);
        var accessor = BuildJsonTextAccessor(field, jsonColumnName);

        return fieldType switch
        {
            "number" or "integer" => BuildNumericCompare(accessor, value, sqlOperator, parameters, ref parameterIndex),
            "string" => BuildDateTimeCompare(accessor, value, sqlOperator, parameters, ref parameterIndex),
            _ => BuildNumericCompare(accessor, value, sqlOperator, parameters, ref parameterIndex)
        };
    }

    private static string BuildNumericCompare(
        string accessor, object? value, string sqlOperator,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);

        if (!decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var numValue))
        {
            throw new ArgumentException($"Value '{stringValue}' is not numeric for comparison operator '{sqlOperator}'");
        }

        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = numValue });
        return $"{accessor}::numeric {sqlOperator} {{{paramIndex}}}";
    }

    private static string BuildDateTimeCompare(
        string accessor, object? value, string sqlOperator,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var stringValue = ConvertToString(value);

        if (!DateTime.TryParse(stringValue, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dateValue))
        {
            throw new ArgumentException($"Value '{stringValue}' is not a valid date/datetime for comparison operator '{sqlOperator}'");
        }

        var paramIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = dateValue, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return $"{accessor}::timestamptz {sqlOperator} {{{paramIndex}}}";
    }

    private static string BuildBetweenCondition(
        string field, object? value, string jsonColumnName,
        List<NpgsqlParameter> parameters, ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
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

        var fieldType = ResolveFieldType(field, schemaContext);
        var accessor = BuildJsonTextAccessor(field, jsonColumnName);

        return fieldType switch
        {
            "number" or "integer" => BuildNumericBetween(accessor, values, parameters, ref parameterIndex),
            "string" => BuildDateTimeBetween(accessor, values, parameters, ref parameterIndex),
            _ => BuildNumericBetween(accessor, values, parameters, ref parameterIndex)
        };
    }

    private static string BuildNumericBetween(
        string accessor, object[] values,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
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
        return $"{accessor}::numeric BETWEEN {{{minIndex}}} AND {{{maxIndex}}}";
    }

    private static string BuildDateTimeBetween(
        string accessor, object[] values,
        List<NpgsqlParameter> parameters, ref int parameterIndex)
    {
        var minString = ConvertToString(values[0]);
        var maxString = ConvertToString(values[1]);

        if (!DateTime.TryParse(minString, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var minDate) ||
            !DateTime.TryParse(maxString, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var maxDate))
        {
            throw new ArgumentException($"Between values must be valid date/datetime: '{minString}', '{maxString}'");
        }

        var minIndex = parameterIndex++;
        var maxIndex = parameterIndex++;
        parameters.Add(new NpgsqlParameter { Value = minDate, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        parameters.Add(new NpgsqlParameter { Value = maxDate, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return $"{accessor}::timestamptz BETWEEN {{{minIndex}}} AND {{{maxIndex}}}";
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


