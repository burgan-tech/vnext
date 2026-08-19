using Microsoft.EntityFrameworkCore;
using Npgsql;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Security;

namespace BBT.Workflow.Definitions.GraphQL;

/// <summary>
/// Unified filter service that automatically handles both legacy and GraphQL-style filters
/// Provides backward compatibility while enabling new GraphQL-style filter features
/// </summary>
public static class UnifiedFilterService
{
    /// <summary>
    /// Apply filters using automatic format detection
    /// Supports both legacy (attributes=field=eq:value) and GraphQL-style JSON formats
    /// </summary>
    /// <typeparam name="T">Entity type</typeparam>
    /// <param name="dbSet">Entity DbSet</param>
    /// <param name="filter">Filter string (legacy or GraphQL-style JSON)</param>
    /// <param name="jsonColumnName">Name of the JSON column</param>
    /// <param name="tableName">Name of the database table</param>
    /// <param name="schema">Database schema name</param>
    /// <returns>Filtered queryable</returns>
    public static IQueryable<T> ApplyFilters<T>(
        this DbSet<T> dbSet,
        string? filter,
        string jsonColumnName = "Data",
        string tableName = "",
        string schema = "public",
        ISchemaValidator? schemaValidator = null,
        SchemaFilterContext? schemaContext = null) where T : class
    {
        if (string.IsNullOrWhiteSpace(filter))
            return dbSet;

        InputValidator.ValidateFilters(filter);

        var format = FilterFormatDetector.DetectFormat(filter);

        return format switch
        {
            FilterFormat.GraphQL => ApplyGraphQLFilters(dbSet, filter, jsonColumnName, tableName, schema, schemaValidator, schemaContext),
            FilterFormat.Legacy => PostgreSqlJsonFilterService.ApplyJsonFilters(dbSet, filter, jsonColumnName, tableName, schema, schemaValidator, schemaContext),

            // Blank input already returned above, so Empty here means "non-blank but unrecognized"
            // — typically a filter truncated so it no longer ends in '}'. Returning dbSet would
            // apply no filter at all and answer every row.
            _ => throw new FilterCompilationException(
                "Filter format is not recognized. Provide GraphQL-style JSON or the legacy " +
                "'field=operator:value' form.")
        };
    }

    private static IQueryable<T> ApplyGraphQLFilters<T>(
        DbSet<T> dbSet,
        string? filter,
        string jsonColumnName,
        string tableName,
        string schema,
        ISchemaValidator? schemaValidator,
        SchemaFilterContext? schemaContext = null) where T : class
    {
        var combinedNode = FilterFormatDetector.CombineFilters(filter);

        if (combinedNode == null)
            return dbSet;

        return dbSet.ApplyGraphQLFilter(combinedNode, jsonColumnName, tableName, schema, schemaValidator, schemaContext: schemaContext);
    }

    /// <summary>
    /// Apply filter with optional aggregation and groupBy support
    /// </summary>
    /// <typeparam name="T">Entity type</typeparam>
    /// <param name="dbContext">Database context</param>
    /// <param name="dbSet">Entity DbSet</param>
    /// <param name="filter">Filter JSON string (GraphQL-style)</param>
    /// <param name="groupBy">GroupBy JSON string</param>
    /// <param name="aggregations">Aggregations JSON string</param>
    /// <param name="jsonColumnName">Name of the JSON column</param>
    /// <param name="schema">Database schema name</param>
    /// <param name="includeFunc">Optional function to include navigation properties</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Filter response with data, aggregations, or groups</returns>
    public static async Task<GraphQLFilterResponse<T>> ApplyFilterWithAggregationsAsync<T>(
        DbContext dbContext,
        DbSet<T> dbSet,
        string? filter,
        string? groupBy,
        string? aggregations,
        string jsonColumnName = "Data",
        string schema = "public",
        Func<IQueryable<T>, IQueryable<T>>? includeFunc = null,
        ISchemaValidator? schemaValidator = null,
        CancellationToken cancellationToken = default,
        SchemaFilterContext? schemaContext = null) where T : class
    {
        var request = GraphQLFilterParser.ParseRequest(filter, groupBy, aggregations);
        request.SchemaContext = schemaContext;
        return await ExecuteRequestAsync(dbContext, dbSet, request, jsonColumnName, schema, includeFunc, applyOrderBy: null, applyOrderByRaw: null, schemaValidator, cancellationToken);
    }

    /// <summary>
    /// Execute a complete GraphQL filter request
    /// </summary>
    /// <param name="includeFunc">Optional function to include navigation properties</param>
    /// <param name="applyOrderBy">Optional function to apply request.OrderBy (instance columns only)</param>
    /// <param name="applyOrderByRaw">Optional function to build ordered query when there is no filter but orderBy has attributes (e.g. raw SQL with ORDER BY subquery)</param>
    public static async Task<GraphQLFilterResponse<T>> ExecuteRequestAsync<T>(
        DbContext dbContext,
        DbSet<T> dbSet,
        GraphQLFilterRequest request,
        string jsonColumnName = "Data",
        string schema = "public",
        Func<IQueryable<T>, IQueryable<T>>? includeFunc = null,
        Func<IQueryable<T>, OrderByRequest?, IQueryable<T>>? applyOrderBy = null,
        Func<DbContext, string, OrderByRequest?, IQueryable<T>>? applyOrderByRaw = null,
        ISchemaValidator? schemaValidator = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var response = new GraphQLFilterResponse<T>();

        if (request.GroupBy != null && request.GroupBy.GetFields().Count > 0)
        {
            response.Groups = await GraphQLAggregationService.ExecuteGroupByAsync(
                dbContext,
                request.Filter,
                request.GroupBy,
                jsonColumnName,
                schema,
                schemaValidator,
                cancellationToken,
                request.SchemaContext);
            
            return response;
        }

        if (request.Aggregations != null && request.Aggregations.HasAggregations)
        {
            response.Aggregations = await GraphQLAggregationService.ExecuteAggregationAsync(
                dbContext,
                request.Filter,
                request.Aggregations,
                jsonColumnName,
                schema,
                schemaValidator,
                cancellationToken,
                request.SchemaContext);
            
            return response;
        }

        var sc = request.SchemaContext;
        var orderByClause = request.OrderBy != null ? GraphQLJsonFilterService.BuildOrderByClause(request.OrderBy, schema, schemaContext: sc) : null;
        var orderByHasAttributes = request.OrderBy != null && request.OrderBy.GetEntries().Any(e => e.Field.Trim().StartsWith("attributes.", StringComparison.OrdinalIgnoreCase));

        IQueryable<T> query;
        if (request.Filter != null)
        {
            query = dbSet.ApplyGraphQLFilter(request.Filter, jsonColumnName, "", schema, schemaValidator, orderByClause: orderByClause, schemaContext: sc);
        }
        else if (orderByHasAttributes && orderByClause != null && applyOrderByRaw != null)
        {
            query = applyOrderByRaw(dbContext, schema, request.OrderBy!);
        }
        else
        {
            query = dbSet;
        }

        // Apply include function if provided
        if (includeFunc != null)
        {
            query = includeFunc(query);
        }

        // Apply orderBy via applicator only when no filter and no attributes (filter/raw path already has ORDER BY)
        if (request.Filter == null && !orderByHasAttributes && request.OrderBy != null && request.OrderBy.GetEntries().Count > 0 && applyOrderBy != null)
        {
            query = applyOrderBy(query, request.OrderBy);
        }

        response.Data = await query.ToListAsync(cancellationToken);

        return response;
    }

    /// <summary>
    /// Build SQL WHERE clause for GraphQL filter (useful for custom queries)
    /// </summary>
    public static (string whereClause, NpgsqlParameter[] parameters) BuildWhereClause(
        string? filterJson,
        string jsonColumnName = "Data")
    {
        if (string.IsNullOrWhiteSpace(filterJson))
            return (string.Empty, Array.Empty<NpgsqlParameter>());

        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);
        if (filterNode == null || filterNode.NodeType == FilterNodeType.Empty)
            return (string.Empty, Array.Empty<NpgsqlParameter>());

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var whereClause = GraphQLJsonFilterService.BuildWhereClause(
            filterNode, jsonColumnName, parameters, ref parameterIndex);

        return (whereClause, parameters.ToArray());
    }

}


