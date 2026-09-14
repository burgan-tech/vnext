using System.Data;
using System.Text;
using BBT.Workflow.Definitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Security;

namespace BBT.Workflow.Definitions.GraphQL;

/// <summary>
/// PostgreSQL aggregation service for GraphQL-style aggregation and groupBy queries
/// Supports COUNT, SUM, AVG, MIN, MAX aggregation functions
/// </summary>
public static class GraphQLAggregationService
{
    /// <summary>
    /// Execute aggregation query without groupBy (returns single aggregation result)
    /// </summary>
    /// <param name="dbContext">Database context</param>
    /// <param name="filterNode">Optional filter node</param>
    /// <param name="aggregations">Aggregation request</param>
    /// <param name="jsonColumnName">Name of the JSON column</param>
    /// <param name="schema">Database schema name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Aggregation response</returns>
    public static async Task<AggregationResponse> ExecuteAggregationAsync(
        DbContext dbContext,
        GraphQLFilterNode? filterNode,
        AggregationRequest aggregations,
        string jsonColumnName = "Data",
        string schema = "public",
        ISchemaValidator? schemaValidator = null,
        CancellationToken cancellationToken = default,
        SchemaFilterContext? schemaContext = null)
    {
        schema = schemaValidator != null
            ? await schemaValidator.ValidateSchemaAsync(schema, cancellationToken)
            : new SyncSchemaValidator().ValidateSchemaSync(schema);

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var (selectClause, _) = BuildAggregationSelectClause(aggregations, jsonColumnName, schemaContext);
        var (jsonWhereClause, instanceWhereClause) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            filterNode, jsonColumnName, parameters, ref parameterIndex, schemaContext: schemaContext);

        var sql = BuildAggregationSql(selectClause, jsonWhereClause, instanceWhereClause, null, schema);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = dbContext.Database.GetDbConnection();

            using var command = connection.CreateCommand();
            command.CommandText = ReplacePlaceholders(sql, parameters.Count);
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            if (dbContext.Database.GetCommandTimeout() is { } timeout)
                command.CommandTimeout = timeout;

            foreach (var param in parameters)
            {
                var npgsqlParam = new NpgsqlParameter
                {
                    Value = param.Value,
                    NpgsqlDbType = param.NpgsqlDbType
                };
                command.Parameters.Add(npgsqlParam);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var response = new AggregationResponse();

            if (await reader.ReadAsync(cancellationToken))
            {
                response = ReadAggregationResult(reader, aggregations);
            }

            return response;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Execute groupBy query with aggregations
    /// </summary>
    /// <param name="dbContext">Database context</param>
    /// <param name="filterNode">Optional filter node</param>
    /// <param name="groupBy">GroupBy request</param>
    /// <param name="jsonColumnName">Name of the JSON column</param>
    /// <param name="schema">Database schema name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of grouped results</returns>
    public static async Task<List<GroupByResponse>> ExecuteGroupByAsync(
        DbContext dbContext,
        GraphQLFilterNode? filterNode,
        GroupByRequest groupBy,
        string jsonColumnName = "Data",
        string schema = "public",
        ISchemaValidator? schemaValidator = null,
        CancellationToken cancellationToken = default,
        SchemaFilterContext? schemaContext = null)
    {
        var groupByFields = groupBy.GetFields();
        if (groupByFields.Count == 0)
            throw new ArgumentException("GroupBy must have at least one field");

        schema = schemaValidator != null
            ? await schemaValidator.ValidateSchemaAsync(schema, cancellationToken)
            : new SyncSchemaValidator().ValidateSchemaSync(schema);

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var (selectClause, groupByClause, needsInstanceJoin) = BuildGroupBySelectClause(
            groupByFields, groupBy.Aggregations ?? new AggregationRequest { Count = true }, jsonColumnName, schemaContext);

        var (jsonWhereClause, instanceWhereClause) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            filterNode, jsonColumnName, parameters, ref parameterIndex, schemaContext: schemaContext);

        var sql = BuildAggregationSql(
            selectClause,
            jsonWhereClause,
            instanceWhereClause,
            groupByClause,
            schema,
            forceInstanceJoin: needsInstanceJoin);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = dbContext.Database.GetDbConnection();

            using var command = connection.CreateCommand();
            command.CommandText = ReplacePlaceholders(sql, parameters.Count);
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            if (dbContext.Database.GetCommandTimeout() is { } timeout)
                command.CommandTimeout = timeout;

            foreach (var param in parameters)
            {
                var npgsqlParam = new NpgsqlParameter
                {
                    Value = param.Value,
                    NpgsqlDbType = param.NpgsqlDbType
                };
                command.Parameters.Add(npgsqlParam);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var results = new List<GroupByResponse>();

            while (await reader.ReadAsync(cancellationToken))
            {
                var group = new GroupByResponse();

                // Read group key values
                for (int i = 0; i < groupByFields.Count; i++)
                {
                    var fieldName = groupByFields[i];
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    group.Keys[fieldName] = value;
                }

                // Read aggregation values (starting after group keys)
                group.Aggregations = ReadAggregationResult(reader, groupBy.Aggregations ?? new AggregationRequest { Count = true }, groupByFields.Count);

                results.Add(group);
            }

            return results;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static (string selectClause, string? groupByClause) BuildAggregationSelectClause(
        AggregationRequest aggregations,
        string jsonColumnName, SchemaFilterContext? schemaContext = null)
    {
        var selectParts = new List<string>();

        if (aggregations.Count != null)
        {
            if (aggregations.Count is bool countBool && countBool)
            {
                selectParts.Add("COUNT(*) AS count_result");
            }
            else if (aggregations.Count is string countField)
            {
                var accessor = ResolveAccessor(countField, jsonColumnName, schemaContext);
                selectParts.Add($"COUNT({accessor}) AS count_result");
            }
            else
            {
                selectParts.Add("COUNT(*) AS count_result");
            }
        }

        if (!string.IsNullOrEmpty(aggregations.Sum))
        {
            var accessor = ResolveAccessor(aggregations.Sum, jsonColumnName, schemaContext, numeric: true);
            selectParts.Add($"SUM({accessor}) AS sum_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Avg))
        {
            var accessor = ResolveAccessor(aggregations.Avg, jsonColumnName, schemaContext, numeric: true);
            selectParts.Add($"AVG({accessor}) AS avg_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Min))
        {
            var accessor = ResolveAccessor(aggregations.Min, jsonColumnName, schemaContext);
            selectParts.Add($"MIN({accessor}) AS min_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Max))
        {
            var accessor = ResolveAccessor(aggregations.Max, jsonColumnName, schemaContext);
            selectParts.Add($"MAX({accessor}) AS max_result");
        }

        if (selectParts.Count == 0)
        {
            selectParts.Add("COUNT(*) AS count_result");
        }

        return (string.Join(", ", selectParts), null);
    }

    internal static (string selectClause, string groupByClause, bool needsInstanceJoin) BuildGroupBySelectClause(
        List<string> groupByFields,
        AggregationRequest aggregations,
        string jsonColumnName, SchemaFilterContext? schemaContext = null)
    {
        var selectParts = new List<string>();
        var groupByParts = new List<string>();
        var needsInstanceJoin = false;

        // Add group by fields to select
        foreach (var field in groupByFields)
        {
            var trimmed = field.Trim();
            var alias = SanitizeAlias(trimmed);
            string accessor;
            if (InstanceFieldDiscriminator.IsInstanceColumn(trimmed))
            {
                var columnName = InstanceFieldDiscriminator.GetInstanceColumnName(trimmed);
                accessor = $"s.\"{columnName}\"";
                needsInstanceJoin = true;
            }
            else
            {
                accessor = ResolveAccessor(trimmed, jsonColumnName, schemaContext);
            }

            selectParts.Add($"{accessor} AS \"{alias}\"");
            groupByParts.Add(accessor);
        }

        // Add aggregation columns
        if (aggregations.Count != null)
        {
            if (aggregations.Count is bool countBool && countBool)
            {
                selectParts.Add("COUNT(*) AS count_result");
            }
            else if (aggregations.Count is string countField)
            {
                var accessor = ResolveAccessor(countField, jsonColumnName, schemaContext);
                selectParts.Add($"COUNT({accessor}) AS count_result");
            }
            else
            {
                selectParts.Add("COUNT(*) AS count_result");
            }
        }

        if (!string.IsNullOrEmpty(aggregations.Sum))
        {
            var accessor = ResolveAccessor(aggregations.Sum, jsonColumnName, schemaContext, numeric: true);
            selectParts.Add($"SUM({accessor}) AS sum_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Avg))
        {
            var accessor = ResolveAccessor(aggregations.Avg, jsonColumnName, schemaContext, numeric: true);
            selectParts.Add($"AVG({accessor}) AS avg_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Min))
        {
            var accessor = ResolveAccessor(aggregations.Min, jsonColumnName, schemaContext);
            selectParts.Add($"MIN({accessor}) AS min_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Max))
        {
            var accessor = ResolveAccessor(aggregations.Max, jsonColumnName, schemaContext);
            selectParts.Add($"MAX({accessor}) AS max_result");
        }

        return (string.Join(", ", selectParts), string.Join(", ", groupByParts), needsInstanceJoin);
    }

    /// <summary>
    /// Builds aggregation/groupBy SQL against <c>InstancesData</c> (alias <c>d</c> when joined) and optional <c>Instances</c> join (<c>s</c>).
    /// </summary>
    /// <param name="forceInstanceJoin">When true, always joins <c>Instances</c> as <c>s</c> (required when grouping or selecting instance columns).</param>
    internal static string BuildAggregationSql(
        string selectClause,
        string jsonWhereClause,
        string? instanceWhereClause,
        string? groupByClause,
        string schema,
        bool forceInstanceJoin = false)
    {
        var sb = new StringBuilder();

        sb.AppendLine($@"SELECT {selectClause}");

        var hasInstanceJoin = forceInstanceJoin || !string.IsNullOrEmpty(instanceWhereClause);
        if (hasInstanceJoin)
        {
            sb.AppendLine($@"FROM ""{schema}"".""InstancesData"" d");
            sb.AppendLine($@"INNER JOIN ""{schema}"".""Instances"" s ON s.""Id"" = d.""InstanceId""");
            sb.AppendLine(@"WHERE d.""IsLatest"" = true");
        }
        else
        {
            sb.AppendLine($@"FROM ""{schema}"".""InstancesData""");
            sb.AppendLine(@"WHERE ""IsLatest"" = true");
        }

        if (!string.IsNullOrEmpty(jsonWhereClause))
            sb.AppendLine($"AND ({jsonWhereClause})");

        if (!string.IsNullOrEmpty(instanceWhereClause))
            sb.AppendLine($"AND ({instanceWhereClause})");

        if (!string.IsNullOrEmpty(groupByClause))
        {
            sb.AppendLine($"GROUP BY {groupByClause}");
            sb.AppendLine($"ORDER BY {groupByClause}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a JSON text accessor for aggregation SQL. Validates path (same rules as filter fields) and escapes literals.
    /// </summary>
    internal static string BuildJsonTextAccessor(string field, string jsonColumnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        InputValidator.ValidateSqlJsonColumnIdentifier(jsonColumnName);

        var path = field.Trim();
        if (path.StartsWith("attributes.", StringComparison.OrdinalIgnoreCase))
            path = path.Substring("attributes.".Length).Trim();

        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("JSON field path cannot be empty.", nameof(field));

        InputValidator.ValidateFieldName(path);

        if (path.Contains('.'))
        {
            var parts = path.Split('.');
            var arrayElements = string.Join(",", parts.Select(p =>
                $"'{InputValidator.EscapePostgresSingleQuotedString(p.Trim())}'"));
            return $"(\"{jsonColumnName}\" #>> ARRAY[{arrayElements}])";
        }

        return $"(\"{jsonColumnName}\" ->> '{InputValidator.EscapePostgresSingleQuotedString(path)}')";
    }

    private static string ResolveAccessor(string field, string column, SchemaFilterContext? context, bool numeric = false)
    {
        var fallback = BuildJsonTextAccessor(field, column);
        if (numeric) fallback = $"({fallback})::numeric";
        var path = field.Trim();
        if (path.StartsWith("attributes.", StringComparison.OrdinalIgnoreCase)) path = path[11..].Trim();
        return column == "Data" ? AttributeSqlExpression.Resolve(path, numeric ? "numeric" : "text", fallback, context) : fallback;
    }

    private static string SanitizeAlias(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            throw new ArgumentException("Field name cannot be null or empty", nameof(field));

        // Remove "attributes." prefix for cleaner aliases
        if (field.StartsWith("attributes.", StringComparison.OrdinalIgnoreCase))
        {
            field = field.Substring("attributes.".Length);
        }

        // Validate and sanitize: only allow alphanumeric, underscores, and dots
        // Replace dots with underscores for SQL identifier safety
        var sanitized = field.Replace('.', '_');

        // Validate that only safe characters remain (alphanumeric and underscores)
        // Must start with letter or underscore
        if (sanitized.Length == 0 || (!char.IsLetter(sanitized[0]) && sanitized[0] != '_'))
        {
            throw new ArgumentException($"Invalid field name for alias: {field}. Must start with letter or underscore.", nameof(field));
        }

        // Check for any invalid characters (only allow alphanumeric and underscores)
        foreach (var c in sanitized)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException($"Invalid character in field name for alias: {field}. Only alphanumeric characters and underscores allowed.", nameof(field));
            }
        }

        return sanitized;
    }

    internal static string ReplacePlaceholders(string sql, int paramCount)
    {
        // Replace {0}, {1}, etc. with $1, $2, etc. for Npgsql
        for (int i = 0; i < paramCount; i++)
        {
            sql = sql.Replace($"{{{i}}}", $"${i + 1}");
        }
        return sql;
    }

    private static AggregationResponse ReadAggregationResult(
        IDataReader reader,
        AggregationRequest aggregations,
        int startIndex = 0)
    {
        var response = new AggregationResponse();
        var columnIndex = startIndex;

        if (aggregations.Count != null)
        {
            if (!reader.IsDBNull(columnIndex))
            {
                response.Count = reader.GetInt64(columnIndex);
            }
            columnIndex++;
        }

        if (!string.IsNullOrEmpty(aggregations.Sum))
        {
            if (!reader.IsDBNull(columnIndex))
            {
                response.Sum = reader.GetDecimal(columnIndex);
            }
            columnIndex++;
        }

        if (!string.IsNullOrEmpty(aggregations.Avg))
        {
            if (!reader.IsDBNull(columnIndex))
            {
                response.Avg = reader.GetDecimal(columnIndex);
            }
            columnIndex++;
        }

        if (!string.IsNullOrEmpty(aggregations.Min))
        {
            if (!reader.IsDBNull(columnIndex))
            {
                response.Min = reader.GetValue(columnIndex);
            }
            columnIndex++;
        }

        if (!string.IsNullOrEmpty(aggregations.Max))
        {
            if (!reader.IsDBNull(columnIndex))
            {
                response.Max = reader.GetValue(columnIndex);
            }
        }

        return response;
    }
}

