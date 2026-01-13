using Microsoft.EntityFrameworkCore;
using Npgsql;
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
    /// <param name="filters">Array of filter strings (can be mixed formats)</param>
    /// <param name="jsonColumnName">Name of the JSON column</param>
    /// <param name="tableName">Name of the database table</param>
    /// <param name="schema">Database schema name</param>
    /// <returns>Filtered queryable</returns>
    public static IQueryable<T> ApplyFilters<T>(
        this DbSet<T> dbSet,
        string[]? filters,
        string jsonColumnName = "Data",
        string tableName = "",
        string schema = "public",
        ISchemaValidator? schemaValidator = null) where T : class
    {
        if (filters == null || filters.Length == 0)
            return dbSet;

        // Validate inputs
        InputValidator.ValidateFilters(filters);

        var format = FilterFormatDetector.DetectFormat(filters);

        return format switch
        {
            FilterFormat.GraphQL => ApplyGraphQLFilters(dbSet, filters, jsonColumnName, tableName, schema, schemaValidator),
            FilterFormat.Legacy => PostgreSqlJsonFilterService.ApplyJsonFilters(dbSet, filters, jsonColumnName, tableName, schema, schemaValidator),
            _ => dbSet
        };
    }

    /// <summary>
    /// Apply GraphQL-style filters
    /// </summary>
    private static IQueryable<T> ApplyGraphQLFilters<T>(
        DbSet<T> dbSet,
        string[] filters,
        string jsonColumnName,
        string tableName,
        string schema,
        ISchemaValidator? schemaValidator) where T : class
    {
        // Combine all filters into a single node
        var combinedNode = FilterFormatDetector.CombineFilters(filters);

        if (combinedNode == null)
            return dbSet;

        return dbSet.ApplyGraphQLFilter(combinedNode, jsonColumnName, tableName, schema, schemaValidator);
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
        CancellationToken cancellationToken = default) where T : class
    {
        var request = GraphQLFilterParser.ParseRequest(filter, groupBy, aggregations);
        return await ExecuteRequestAsync(dbContext, dbSet, request, jsonColumnName, schema, includeFunc, schemaValidator, cancellationToken);
    }

    /// <summary>
    /// Execute a complete GraphQL filter request
    /// </summary>
    /// <param name="includeFunc">Optional function to include navigation properties</param>
    public static async Task<GraphQLFilterResponse<T>> ExecuteRequestAsync<T>(
        DbContext dbContext,
        DbSet<T> dbSet,
        GraphQLFilterRequest request,
        string jsonColumnName = "Data",
        string schema = "public",
        Func<IQueryable<T>, IQueryable<T>>? includeFunc = null,
        ISchemaValidator? schemaValidator = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var response = new GraphQLFilterResponse<T>();

        // Handle GroupBy with aggregations
        if (request.GroupBy != null && request.GroupBy.GetFields().Count > 0)
        {
            response.Groups = await GraphQLAggregationService.ExecuteGroupByAsync(
                dbContext,
                request.Filter,
                request.GroupBy,
                jsonColumnName,
                schema,
                schemaValidator,
                cancellationToken);
            
            return response;
        }

        // Handle aggregations without GroupBy
        if (request.Aggregations != null && request.Aggregations.HasAggregations)
        {
            response.Aggregations = await GraphQLAggregationService.ExecuteAggregationAsync(
                dbContext,
                request.Filter,
                request.Aggregations,
                jsonColumnName,
                schema,
                schemaValidator,
                cancellationToken);
            
            return response;
        }

        // Handle regular filter (no aggregations)
        var query = request.Filter != null
            ? dbSet.ApplyGraphQLFilter(request.Filter, jsonColumnName, "", schema, schemaValidator)
            : dbSet;

        // Apply include function if provided
        if (includeFunc != null)
        {
            query = includeFunc(query);
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

    /// <summary>
    /// Validates a GraphQL filter JSON string without executing it
    /// </summary>
    /// <param name="filterJson">Filter JSON to validate</param>
    /// <returns>Validation result with error message if invalid</returns>
    public static (bool isValid, string? errorMessage) ValidateFilter(string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson))
            return (true, null);

        try
        {
            var node = GraphQLFilterParser.ParseFilter(filterJson);
            
            if (node == null)
                return (true, null); // Empty filter is valid

            // Validate the filter structure
            return ValidateNode(node);
        }
        catch (ArgumentException ex)
        {
            return (false, ex.Message);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return (false, $"Invalid JSON filter format: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Invalid filter format: {ex.Message}");
        }
    }

    private static (bool isValid, string? errorMessage) ValidateNode(GraphQLFilterNode node)
    {
        switch (node.NodeType)
        {
            case FilterNodeType.And:
                if (node.And == null || node.And.Count == 0)
                    return (false, "AND operator requires at least one condition");
                
                foreach (var child in node.And)
                {
                    var (isValid, error) = ValidateNode(child);
                    if (!isValid)
                        return (false, error);
                }
                break;

            case FilterNodeType.Or:
                if (node.Or == null || node.Or.Count == 0)
                    return (false, "OR operator requires at least one condition");
                
                foreach (var child in node.Or)
                {
                    var (isValid, error) = ValidateNode(child);
                    if (!isValid)
                        return (false, error);
                }
                break;

            case FilterNodeType.Not:
                if (node.Not == null)
                    return (false, "NOT operator requires a condition");
                
                var (notValid, notError) = ValidateNode(node.Not);
                if (!notValid)
                    return (false, notError);
                break;

            case FilterNodeType.Condition:
                if (node.Attributes == null || node.Attributes.Count == 0)
                    return (false, "Condition must have at least one field");
                
                foreach (var (fieldName, condition) in node.Attributes)
                {
                    if (string.IsNullOrEmpty(fieldName))
                        return (false, "Field name cannot be empty");

                    if (!condition.GetOperators().Any() && condition.NestedConditions == null)
                        return (false, $"Field '{fieldName}' must have at least one operator");
                }
                break;
            default:
                // Unknown node type, consider invalid
                return (false, $"Unknown node type: {node.NodeType}");
        }

        return (true, null);
    }
}


