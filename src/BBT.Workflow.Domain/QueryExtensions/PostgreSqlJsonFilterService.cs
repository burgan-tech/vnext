using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Security;

namespace BBT.Workflow.Definitions;

/// <summary>
/// PostgreSQL native JSONB filter service using FromSqlRaw for optimal performance
/// Supports all numeric operations without range limitations
/// </summary>
public static class PostgreSqlJsonFilterService
{
    /// <summary>
    /// Apply JSON filters using PostgreSQL native JSONB operators with CTE approach
    /// </summary>
    /// <typeparam name="T">Entity type</typeparam>
    /// <param name="dbSet">Entity DbSet</param>
    /// <param name="filter">Filter string in format: "field=operator:value"</param>
    /// <param name="jsonColumnName">Name of the JSON column (e.g., "Data", "Json")</param>
    /// <param name="tableName">Name of the database table</param>
    /// <param name="schema">Database schema name</param>
    /// <param name="schemaValidator">Optional schema validator for security validation</param>
    /// <param name="logger">Optional logger for error tracking</param>
    /// <returns>Filtered queryable</returns>
    public static IQueryable<T> ApplyJsonFilters<T>(
        this DbSet<T> dbSet,
        string? filter,
        string jsonColumnName = "Data",
        string tableName = "",
        string schema = "public",
        ISchemaValidator? schemaValidator = null,
        SchemaFilterContext? schemaContext = null,
        ILogger? logger = null) where T : class
    {
        if (string.IsNullOrWhiteSpace(filter))
            return dbSet;

        // Validate inputs
        InputValidator.ValidateFilters(filter);

        var filters = new[] { filter };
        
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

        // Separate Instance column filters from JSON Data filters
        var (instanceFilters, jsonFilters) = InstanceFieldDiscriminator.SeparateFilters(filters);

        var jsonWhereConditions = new List<string>();
        var instanceWhereConditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Process JSON Data filters. Parse failures propagate: dropping a condition here left the
        // query broader than the caller asked for, and the logger is null on the hot path so it
        // happened silently.
        foreach (var filterItem in jsonFilters)
        {
            var (field, operatorType, operatorValue) = FilterOperatorParser.ParseOperator(filterItem);

            var (condition, filterParameters) = BuildPostgreSqlCondition(
                field, operatorType, operatorValue, jsonColumnName, ref parameterIndex, schemaContext);

            if (!string.IsNullOrEmpty(condition))
            {
                jsonWhereConditions.Add(condition);
                parameters.AddRange(filterParameters);
            }
        }

        // Process Instance column filters
        foreach (var filterItem in instanceFilters)
        {
            var (field, operatorType, operatorValue) = FilterOperatorParser.ParseOperator(filterItem);

            var (condition, filterParameters) = InstanceColumnConditionBuilder.BuildCondition(
                field, operatorType, operatorValue, ref parameterIndex);

            if (!string.IsNullOrEmpty(condition))
            {
                instanceWhereConditions.Add(condition);
                parameters.AddRange(filterParameters);
            }
        }

        // A non-blank filter that compiled to nothing means the caller's conditions were dropped.
        // Returning the unfiltered set here would answer every row to a narrowing query — the same
        // hole GraphQLJsonFilterService guards against.
        if (!jsonWhereConditions.Any() && !instanceWhereConditions.Any())
        {
            throw new FilterCompilationException(
                "Filter could not be translated into any condition. No results can be returned safely; " +
                "check the filter's operators and field names.");
        }

        // Get table name if not provided
        if (string.IsNullOrEmpty(tableName))
        {
            tableName = typeof(T).Name + "s"; // Default convention: Entity + "s"
        }

        // Build CTE-based SQL query with both JSON and Instance filters
        var jsonWhereClause = jsonWhereConditions.Any() 
            ? string.Join(" AND ", jsonWhereConditions) 
            : string.Empty;
        
        var instanceWhereClause = instanceWhereConditions.Any()
            ? string.Join(" AND ", instanceWhereConditions)
            : string.Empty;

        // Build SQL with conditional WHERE clauses
        // If we have JSON filters, include them in the CTE WHERE clause
        // If we have Instance filters, include them in the outer WHERE clause
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
            ORDER BY s.""CreatedAt"" DESC, s.""Id"" ASC";

        // Use exact working pattern - NpgsqlParameter array without @ in SQL
        return dbSet.FromSqlRaw(rawSql, parameters.ToArray()).AsNoTracking();
    }

    /// <summary>
    /// Apply single JSON filter using PostgreSQL native JSONB operators
    /// </summary>
    public static IQueryable<T> ApplyJsonFilter<T>(
        this DbSet<T> dbSet,
        string field,
        string operatorType,
        string value,
        string jsonColumnName = "Json",
        string tableName = "") where T : class
    {
        return dbSet.ApplyJsonFilters(
            $"{field}={operatorType}:{value}",
            jsonColumnName,
            tableName);
    }

    /// <summary>
    /// Create filtered SQL query string with parameters (for manual execution)
    /// </summary>
    public static (string sql, NpgsqlParameter[] parameters) BuildFilteredQuery<T>(
        string[] filters,
        string jsonColumnName = "Json",
        string tableName = "") where T : class
    {
        if (filters == null || !filters.Any())
            return ("", Array.Empty<NpgsqlParameter>());

        var whereConditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Parse failures propagate. Silently skipping an invalid filter produced a query broader
        // than the caller authored, with no signal that anything was discarded.
        foreach (var filter in filters)
        {
            var (field, operatorType, operatorValue) = FilterOperatorParser.ParseOperator(filter);

            var (condition, filterParameters) = BuildPostgreSqlCondition(
                field, operatorType, operatorValue, jsonColumnName, ref parameterIndex);

            if (!string.IsNullOrEmpty(condition))
            {
                whereConditions.Add(condition);
                parameters.AddRange(filterParameters);
            }
        }

        if (!whereConditions.Any())
            return ("", Array.Empty<NpgsqlParameter>());

        if (string.IsNullOrEmpty(tableName))
        {
            tableName = typeof(T).Name + "s";
        }

        var whereClause = string.Join(" AND ", whereConditions);
        var sql = $"SELECT * FROM \"{tableName}\" WHERE {whereClause}";

        return (sql, parameters.ToArray());
    }

    private static (string condition, List<NpgsqlParameter> parameters) BuildPostgreSqlCondition(
        string field,
        string operatorType,
        string value,
        string jsonColumnName,
        ref int parameterIndex,
        SchemaFilterContext? schemaContext = null)
    {
        var sanitizedField = SanitizeFieldName(field);
        var parameters = new List<NpgsqlParameter>();
        
        if (schemaContext != null && (!schemaContext.IsFieldFilterable(sanitizedField) ||
            !schemaContext.IsOperatorAllowed(sanitizedField, operatorType)))
            throw new SchemaFilterValidationException($"Operator '{operatorType}' is not allowed for field '{sanitizedField}'.");
        var condition = AttributeConditionBuilder.BuildOperatorCondition(sanitizedField, operatorType,
            value, jsonColumnName, parameters, ref parameterIndex, schemaContext);
        return (condition, parameters);
    }

    private static string SanitizeFieldName(string field) => AttributeConditionBuilder.SanitizeFieldName(field);
}
