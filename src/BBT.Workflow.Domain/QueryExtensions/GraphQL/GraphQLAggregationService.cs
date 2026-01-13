using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
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
        CancellationToken cancellationToken = default)
    {
        // Validate schema
        schema = schemaValidator != null
            ? await schemaValidator.ValidateSchemaAsync(schema, cancellationToken)
            : new SyncSchemaValidator().ValidateSchemaSync(schema);

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var (selectClause, _) = BuildAggregationSelectClause(aggregations, jsonColumnName);
        var whereClause = filterNode != null 
            ? GraphQLJsonFilterService.BuildWhereClause(filterNode, jsonColumnName, parameters, ref parameterIndex)
            : string.Empty;

        var sql = BuildAggregationSql(selectClause, whereClause, null, schema);

        using var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = ReplacePlaceholders(sql, parameters.Count);
        
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
        CancellationToken cancellationToken = default)
    {
        var groupByFields = groupBy.GetFields();
        if (groupByFields.Count == 0)
            throw new ArgumentException("GroupBy must have at least one field");

        // Validate schema
        schema = schemaValidator != null
            ? await schemaValidator.ValidateSchemaAsync(schema, cancellationToken)
            : new SyncSchemaValidator().ValidateSchemaSync(schema);

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var (selectClause, groupByClause) = BuildGroupBySelectClause(
            groupByFields, groupBy.Aggregations ?? new AggregationRequest { Count = true }, jsonColumnName);
        
        var whereClause = filterNode != null 
            ? GraphQLJsonFilterService.BuildWhereClause(filterNode, jsonColumnName, parameters, ref parameterIndex)
            : string.Empty;

        var sql = BuildAggregationSql(selectClause, whereClause, groupByClause, schema);

        using var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = ReplacePlaceholders(sql, parameters.Count);
        
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

    private static (string selectClause, string? groupByClause) BuildAggregationSelectClause(
        AggregationRequest aggregations,
        string jsonColumnName)
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
                var accessor = BuildJsonTextAccessor(countField, jsonColumnName);
                selectParts.Add($"COUNT({accessor}) AS count_result");
            }
            else
            {
                selectParts.Add("COUNT(*) AS count_result");
            }
        }

        if (!string.IsNullOrEmpty(aggregations.Sum))
        {
            var accessor = BuildJsonTextAccessor(aggregations.Sum, jsonColumnName);
            selectParts.Add($"SUM(({accessor})::numeric) AS sum_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Avg))
        {
            var accessor = BuildJsonTextAccessor(aggregations.Avg, jsonColumnName);
            selectParts.Add($"AVG(({accessor})::numeric) AS avg_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Min))
        {
            var accessor = BuildJsonTextAccessor(aggregations.Min, jsonColumnName);
            selectParts.Add($"MIN({accessor}) AS min_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Max))
        {
            var accessor = BuildJsonTextAccessor(aggregations.Max, jsonColumnName);
            selectParts.Add($"MAX({accessor}) AS max_result");
        }

        if (selectParts.Count == 0)
        {
            selectParts.Add("COUNT(*) AS count_result");
        }

        return (string.Join(", ", selectParts), null);
    }

    private static (string selectClause, string groupByClause) BuildGroupBySelectClause(
        List<string> groupByFields,
        AggregationRequest aggregations,
        string jsonColumnName)
    {
        var selectParts = new List<string>();
        var groupByParts = new List<string>();

        // Add group by fields to select
        foreach (var field in groupByFields)
        {
            var accessor = BuildJsonTextAccessor(field, jsonColumnName);
            selectParts.Add($"{accessor} AS \"{SanitizeAlias(field)}\"");
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
                var accessor = BuildJsonTextAccessor(countField, jsonColumnName);
                selectParts.Add($"COUNT({accessor}) AS count_result");
            }
            else
            {
                selectParts.Add("COUNT(*) AS count_result");
            }
        }

        if (!string.IsNullOrEmpty(aggregations.Sum))
        {
            var accessor = BuildJsonTextAccessor(aggregations.Sum, jsonColumnName);
            selectParts.Add($"SUM(({accessor})::numeric) AS sum_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Avg))
        {
            var accessor = BuildJsonTextAccessor(aggregations.Avg, jsonColumnName);
            selectParts.Add($"AVG(({accessor})::numeric) AS avg_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Min))
        {
            var accessor = BuildJsonTextAccessor(aggregations.Min, jsonColumnName);
            selectParts.Add($"MIN({accessor}) AS min_result");
        }

        if (!string.IsNullOrEmpty(aggregations.Max))
        {
            var accessor = BuildJsonTextAccessor(aggregations.Max, jsonColumnName);
            selectParts.Add($"MAX({accessor}) AS max_result");
        }

        return (string.Join(", ", selectParts), string.Join(", ", groupByParts));
    }

    private static string BuildAggregationSql(
        string selectClause,
        string whereClause,
        string? groupByClause,
        string schema)
    {
        var sb = new StringBuilder();

        sb.AppendLine($@"SELECT {selectClause}");
        sb.AppendLine($@"FROM ""{schema}"".""InstancesData""");
        sb.AppendLine(@"WHERE ""IsLatest"" = true");

        if (!string.IsNullOrEmpty(whereClause))
        {
            sb.AppendLine($"AND ({whereClause})");
        }

        if (!string.IsNullOrEmpty(groupByClause))
        {
            sb.AppendLine($"GROUP BY {groupByClause}");
            sb.AppendLine($"ORDER BY {groupByClause}");
        }

        return sb.ToString();
    }

    private static string BuildJsonTextAccessor(string field, string jsonColumnName)
    {
        // Handle "attributes." prefix if present
        if (field.StartsWith("attributes.", StringComparison.OrdinalIgnoreCase))
        {
            field = field.Substring("attributes.".Length);
        }

        if (field.Contains('.'))
        {
            var parts = field.Split('.');
            var arrayElements = string.Join(",", parts.Select(p => $"'{p}'"));
            return $"(\"{jsonColumnName}\" #>> ARRAY[{arrayElements}])";
        }
        
        return $"(\"{jsonColumnName}\" ->> '{field}')";
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

    private static string ReplacePlaceholders(string sql, int paramCount)
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


