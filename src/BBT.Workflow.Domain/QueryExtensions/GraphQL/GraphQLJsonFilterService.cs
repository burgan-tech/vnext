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
        SchemaFilterContext? schemaContext = null,
        int? offset = null, int? limit = null) where T : class
    {
        var (sql, parameters) = BuildInstanceQuery(filterNode, jsonColumnName, tableName, schema,
            schemaValidator, logger, orderByClause, schemaContext, offset, limit);
        return dbSet.FromSqlRaw(sql, parameters).AsNoTracking();
    }

    /// <summary>Compiles the instance query once for entity and narrow identity-page execution.</summary>
    internal static (string Sql, NpgsqlParameter[] Parameters) BuildInstanceQuery(
        GraphQLFilterNode filterNode, string jsonColumnName, string tableName, string schema,
        ISchemaValidator? schemaValidator = null, ILogger? logger = null,
        string? orderByClause = null, SchemaFilterContext? schemaContext = null,
        int? offset = null, int? limit = null, bool useLatestJoin = false)
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

        var (jsonWhereClause, instanceWhereClause) = RequiresUnifiedPredicate(filterNode)
            ? (string.Empty, BuildUnifiedPredicate(filterNode, jsonColumnName, parameters, ref parameterIndex, schemaContext, schema, useLatestJoin))
            : BuildSeparatedWhereClauses(filterNode, jsonColumnName, parameters, ref parameterIndex, logger, schemaContext);

        if (string.IsNullOrEmpty(jsonWhereClause) && string.IsNullOrEmpty(instanceWhereClause))
        {
            // An Empty node carries no conditions, so returning the unfiltered set is correct.
            // Any other node type compiling to nothing means the caller's conditions were dropped
            // somewhere below — returning dbSet there would answer every row to a narrowing query.
            if (filterNode.NodeType != FilterNodeType.Empty)
            {
                throw new FilterCompilationException(
                    "Filter could not be translated into any condition. No results can be returned safely; " +
                    "check the filter's operators and field names.");
            }


        }

        if (string.IsNullOrEmpty(tableName))
        {
            tableName = "Instances";
        }

        var hasJsonFilter = !string.IsNullOrEmpty(jsonWhereClause);
        var hasInstanceFilter = !string.IsNullOrEmpty(instanceWhereClause);
        var orderBy = string.IsNullOrWhiteSpace(orderByClause) ? "s.\"CreatedAt\" DESC, s.\"Id\" ASC" : orderByClause;

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
        else if (hasInstanceFilter)
        {
            rawSql = $@"
            SELECT s.*
            FROM ""{schema}"".""Instances"" s
            WHERE {instanceWhereClause}
            ORDER BY {orderBy}";
        }

        else
        {
            rawSql = $"SELECT s.* FROM \"{schema}\".\"Instances\" s ORDER BY {orderBy}";
        }

        if (useLatestJoin && (hasJsonFilter || RequiresUnifiedPredicate(filterNode) ||
                              orderBy.Contains("_latest.", StringComparison.Ordinal)))
        {
            // The unique partial InstanceId index guarantees at most one latest row. A LEFT
            // JOIN preserves instances without data for mixed predicates and attribute sorting.
            var join = hasJsonFilter ? "JOIN" : "LEFT JOIN";
            var predicate = hasJsonFilter ? jsonWhereClause : string.Empty;
            if (hasInstanceFilter)
                predicate = predicate.Length == 0 ? instanceWhereClause : $"({predicate}) AND ({instanceWhereClause})";
            rawSql = $"SELECT s.* FROM \"{schema}\".\"Instances\" s {join} \"{schema}\".\"InstancesData\" _latest " +
                     $"ON _latest.\"InstanceId\" = s.\"Id\" AND _latest.\"IsLatest\" = true" +
                     (predicate.Length == 0 ? "" : $" WHERE {predicate}") + $" ORDER BY {orderBy}";
        }

        if (offset.HasValue || limit.HasValue)
        {
            if (offset is null or < 0 || limit is null or < 1)
                throw new ArgumentOutOfRangeException(nameof(offset));
            var limitIndex = parameterIndex++;
            var offsetIndex = parameterIndex++;
            parameters.Add(new NpgsqlParameter { Value = limit.Value });
            parameters.Add(new NpgsqlParameter { Value = offset.Value });
            rawSql += $" LIMIT {{{limitIndex}}} OFFSET {{{offsetIndex}}}";
        }
        return (rawSql, parameters.ToArray());
    }

    /// <summary>
    /// Builds ORDER BY clause SQL for instance list (instance columns and/or attributes JSON path).
    /// </summary>
    public static string? BuildOrderByClause(
        OrderByRequest? orderBy,
        string schema,
        string instanceAlias = "s",
        string dataTableName = "InstancesData",
        SchemaFilterContext? schemaContext = null,
        string? latestAlias = null)
    {
        if (orderBy == null)
            return null;
        var entries = orderBy.GetEntries();
        if (entries.Count == 0)
            return null;

        // Every entry must produce a clause. Skipping unusable entries used to leave parts empty,
        // which fell back to s."CreatedAt" DESC — the caller got HTTP 200 with results in an order
        // they never asked for.
        var parts = new List<string>();
        foreach (var (field, direction) in entries)
        {
            var dir = direction.Equals("desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            var trimmed = field.Trim();
            if (trimmed.StartsWith("attributes.", StringComparison.OrdinalIgnoreCase))
            {
                var jsonPath = trimmed.Substring("attributes.".Length).Trim();
                if (string.IsNullOrEmpty(jsonPath) || !IsSafeJsonPath(jsonPath))
                {
                    // Boundary validation owns this rule (sort.unsafePath); reaching it here means
                    // the two have drifted, not that the caller broke a schema policy.
                    throw new FilterCompilationException(
                        $"Sort field '{field}' is not a valid attributes path.");
                }

                // Schema policy, not drift: sortability lives in the master schema and is
                // deliberately not duplicated in the boundary validator.
                if (schemaContext != null && !schemaContext.IsFieldSortable(jsonPath))
                {
                    throw new SchemaFilterValidationException(
                        $"Field '{jsonPath}' is not sortable.");
                }

                var accessor = AttributeSqlExpression.Resolve(jsonPath, "text", BuildJsonTextAccessorForOrderBy(jsonPath), schemaContext);
                parts.Add(latestAlias == null
                    ? $"(SELECT {accessor} FROM \"{schema}\".\"{dataTableName}\" _d WHERE _d.\"InstanceId\" = {instanceAlias}.\"Id\" AND _d.\"IsLatest\" = true LIMIT 1) {dir}"
                    : $"{(accessor.Contains("\"Data\"", StringComparison.Ordinal) ? accessor.Replace("\"Data\"", $"{latestAlias}.\"Data\"") : $"{latestAlias}.{accessor}")} {dir}");
            }
            else if (InstanceFieldDiscriminator.IsInstanceColumn(trimmed))
            {
                var columnName = InstanceFieldDiscriminator.GetInstanceColumnName(trimmed);
                parts.Add($"{instanceAlias}.\"{columnName}\" {dir}");
            }
            else
            {
                // Boundary validation owns this rule too (sort.unknownField).
                throw new FilterCompilationException(
                    $"Sort field '{field}' is neither an instance column nor an 'attributes.' path.");
            }
        }
        if (parts.Count == 0)
            return null;
        if (!entries.Any(e => e.Field.Trim().Equals("id", StringComparison.OrdinalIgnoreCase)))
            parts.Add($"{instanceAlias}.\"Id\" ASC");
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

        return RequiresUnifiedPredicate(filterNode)
            ? (string.Empty, BuildUnifiedPredicate(filterNode, jsonColumnName, parameters, ref parameterIndex, schemaContext))
            : BuildSeparatedWhereClauses(filterNode, jsonColumnName, parameters, ref parameterIndex, logger, schemaContext);
    }

    private static bool RequiresUnifiedPredicate(GraphQLFilterNode node)
    {
        var (instance, json, logical) = Inspect(node);
        return instance && json && logical;
    }

    private static (bool Instance, bool Json, bool Logical) Inspect(GraphQLFilterNode node)
    {
        var instance = node.Attributes?.Keys.Any(InstanceFieldDiscriminator.IsInstanceColumn) == true;
        var json = node.Attributes?.Keys.Any(k => !InstanceFieldDiscriminator.IsInstanceColumn(k)) == true;
        var logical = node.NodeType is FilterNodeType.Or or FilterNodeType.Not;
        var children = node.And ?? node.Or ?? (node.Not == null ? [] : new List<GraphQLFilterNode> { node.Not });
        foreach (var child in children)
        {
            var c = Inspect(child);
            instance |= c.Instance; json |= c.Json; logical |= c.Logical;
        }
        return (instance, json, logical);
    }

    private static string BuildUnifiedPredicate(GraphQLFilterNode node, string column,
        List<NpgsqlParameter> parameters, ref int index, SchemaFilterContext? context, string? schema = null, bool useLatestJoin = false)
    {
        if (node.NodeType == FilterNodeType.Not)
            return $"NOT ({BuildUnifiedPredicate(node.Not!, column, parameters, ref index, context, schema, useLatestJoin)})";
        var parts = new List<string>();
        if (node.NodeType is FilterNodeType.And or FilterNodeType.Or)
        {
            foreach (var child in (node.And ?? node.Or)!)
                parts.Add(BuildUnifiedPredicate(child, column, parameters, ref index, context, schema, useLatestJoin));
            return "(" + string.Join(node.NodeType == FilterNodeType.Or ? " OR " : " AND ", parts) + ")";
        }
        if (node.Attributes == null) throw new FilterCompilationException("Empty logical filter node.");
        foreach (var (field, condition) in node.Attributes)
        {
            if (InstanceFieldDiscriminator.IsInstanceColumn(field))
                parts.AddRange(BuildInstanceFieldConditions(field, condition, parameters, ref index, null));
            else
            {
                var predicate = string.Join(" AND ", BuildFieldConditions(field, condition, column, parameters, ref index, context));
                if (string.IsNullOrEmpty(predicate)) throw new FilterCompilationException("Empty attribute predicate.");
                parts.Add(useLatestJoin ? $"(CASE WHEN _latest.\"Id\" IS NULL THEN NULL ELSE ({predicate}) END)" :
                    schema == null ? $"({predicate})" :
                    $"(SELECT ({predicate}) FROM \"{schema}\".\"InstancesData\" WHERE \"InstanceId\" = s.\"Id\" AND \"IsLatest\" = true LIMIT 1)");
            }
        }
        return "(" + string.Join(" AND ", parts) + ")";
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

        // Process each operator in the condition.
        //
        // Failures deliberately propagate. These used to be caught and logged at Warning, but the
        // hot path passes a null logger, so an unusable condition (e.g. createdAt eq "notadate")
        // was dropped in total silence and the query ran without it — returning every row.
        foreach (var (op, value) in condition.GetOperators())
        {
            // Array-valued operators (in, nin, between) arrive as object[] from the JSON
            // filter syntax. InstanceColumnConditionBuilder expects a comma-separated
            // string, so flatten the array element-by-element instead of calling
            // ConvertToString on the array itself (which would yield "System.Object[]").
            var stringValue = ConvertOperatorValueToString(value);
            var (conditionSql, conditionParams) = InstanceColumnConditionBuilder.BuildCondition(
                columnName, op, stringValue, ref parameterIndex);

            if (!string.IsNullOrEmpty(conditionSql))
            {
                conditions.Add(conditionSql);
                parameters.AddRange(conditionParams);
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

        if (schemaContext != null && condition.GetOperators().Any() && !schemaContext.IsFieldFilterable(sanitizedField))
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

    private static string BuildOperatorCondition(string field, string op, object? value,
        string column, List<NpgsqlParameter> parameters, ref int index, SchemaFilterContext? context = null)
        => AttributeConditionBuilder.BuildOperatorCondition(field, op, value, column, parameters, ref index, context);

    private static string SanitizeFieldName(string field) => AttributeConditionBuilder.SanitizeFieldName(field);
    private static string ConvertOperatorValueToString(object? value) => AttributeConditionBuilder.ConvertOperatorValueToString(value);
}
