using System.Text.Json;
using BBT.Aether;
using BBT.Aether.Domain;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances;

public sealed class EfCoreInstanceRepository(
    IDbContextProvider<WorkflowDbContext> dbContext,
    IServiceProvider serviceProvider,
    IWorkflowMetrics workflowMetrics,
    IRuntimeInfoProvider runtimeInfoProvider,
    IDataSinkManager dataSinkManager,
     ICurrentSchema currentSchema,
    ISchemaValidator schemaValidator,
    ILogger<EfCoreInstanceRepository> logger)
    : EfCoreRepository<WorkflowDbContext, Instance, Guid>(dbContext, serviceProvider),
        IInstanceRepository
{
    public override async Task<IQueryable<Instance>> WithDetailsAsync()
    {
        // Runtime only consumes the latest InstanceData snapshot and active (non-completed)
        // child correlations. Filtered includes keep the join sets minimal so the partial
        // indexes UX_InstancesData_Instance_IsLatest and IX_InstancesCorrelations_ActiveByParent_Covering
        // can serve these reads as index-only scans.
        return (await base.WithDetailsAsync())
            .Include(i => i.DataList.Where(d => d.IsLatest))
            .Include(i => i.ChildCorrelations.Where(c => !c.IsCompleted));
    }

    /// <summary>
    /// Inserts a new instance and automatically records metrics
    /// </summary>
    public override async Task<Instance> InsertAsync(Instance entity, bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        var result = await base.InsertAsync(entity, autoSave, cancellationToken);

        // Database metrics are automatically recorded by WorkflowDatabaseInterceptor
        // Only record business-specific instance metrics here
        workflowMetrics.RecordInstanceCreated(entity.Flow, runtimeInfoProvider.Domain);

        // Transfer to data sinks (e.g., ClickHouse) if enabled
        try
        {
            await dataSinkManager.HandleInsertAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
            // Log error but don't fail the main operation
            logger.LogWarning(ex, "Failed to transfer instance to data sinks");
        }

        return result;
    }

    /// <summary>
    /// Updates an instance and automatically records status change metrics
    /// </summary>
    public override async Task<Instance> UpdateAsync(Instance entity, bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        // Get the original entity to compare status changes
        var originalEntity = await FindAsync(entity.Id, includeDetails: false, cancellationToken);
        var originalStatus = originalEntity?.Status;

        var result = await base.UpdateAsync(entity, autoSave, cancellationToken);

        // Database metrics are automatically recorded by WorkflowDatabaseInterceptor
        // Only handle business-specific status change metrics here
        if (originalStatus != null && !originalStatus.Equals(entity.Status))
        {
            await HandleStatusChangeMetrics(entity, originalStatus, entity.Status);
        }

        // Transfer to data sinks (e.g., ClickHouse) if enabled
        try
        {
            await dataSinkManager.HandleUpdateAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
            // Log error but don't fail the main operation
            logger.LogWarning(ex, "Failed to transfer instance to data sinks");
        }

        return result;
    }

    // Transaction metrics are now automatically handled by WorkflowDatabaseInterceptor
    // No need for manual transaction tracking helpers

    public async Task<Instance?> FindByIdentifierAsync(
        string? identifier,
        CancellationToken cancellationToken = default)
    {
        var query = (await WithDetailsAsync())
            .AsSplitQuery();

        if (Guid.TryParse(identifier, out var instanceId))
        {
            var response = await query
               .FirstOrDefaultAsync(
                   p => p.Id == instanceId,
                   cancellationToken);
            if (response != null)
            {
                return response;
            }
        }

        return await query
            .FirstOrDefaultAsync(
                p => p.Key == identifier,
                cancellationToken);
    }

    public async Task<Instance?> FindByIdentifierAsReadOnlyAsync(string identifier,
        CancellationToken cancellationToken = default)
    {
        var query = (await WithDetailsAsync())
            .AsNoTracking()
            .AsSplitQuery();

        if (Guid.TryParse(identifier, out var instanceId))
        {
            var response = await query
                .FirstOrDefaultAsync(
                    p => p.Id == instanceId,
                    cancellationToken);
            if (response != null)
            {
                return response;
            }
        }

        return await query
            .FirstOrDefaultAsync(
                p => p.Key == identifier,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Instance?> FindByIdentifierWithFullHistoryAsync(string identifier,
        CancellationToken cancellationToken = default)
    {
        // Bypass WithDetailsAsync because its DataList include is filtered to
        // IsLatest = true. GetInstanceHistoryAsync needs every InstanceData revision
        // to enumerate the full transition history.
        var dbSet = await GetDbSetAsync();
        var query = dbSet
            .Include(i => i.DataList)
            .Include(i => i.ChildCorrelations.Where(c => !c.IsCompleted))
            .AsNoTracking()
            .AsSplitQuery();

        if (Guid.TryParse(identifier, out var instanceId))
        {
            var response = await query
                .FirstOrDefaultAsync(
                    p => p.Id == instanceId,
                    cancellationToken);
            if (response != null)
            {
                return response;
            }
        }

        return await query
            .FirstOrDefaultAsync(
                p => p.Key == identifier,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Instance?> FindByIdentifierWithFullDataAsync(string? identifier,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        var query = dbSet
            .Include(i => i.DataList)
            .Include(i => i.ChildCorrelations.Where(c => !c.IsCompleted))
            .AsSplitQuery();

        if (Guid.TryParse(identifier, out var instanceId))
        {
            var response = await query
                .FirstOrDefaultAsync(
                    p => p.Id == instanceId,
                    cancellationToken);
            if (response != null)
            {
                return response;
            }
        }

        return await query
            .FirstOrDefaultAsync(
                p => p.Key == identifier,
                cancellationToken);
    }

    /// <summary>
    /// Finds active instance data with smart version matching.
    /// </summary>
    /// <param name="key">The instance key to search for</param>
    /// <param name="version">The version to search for. Supports:
    /// <list type="bullet">
    ///     <item><description>Full version (e.g., "1.0.0-pkg.1.17.0+account"): Exact match</description></item>
    ///     <item><description>Artifact version (e.g., "1.0.0" or "1.0.0-alpha.1"): Returns highest pkg version for that artifact</description></item>
    ///     <item><description>Partial version (e.g., "1.0"): Returns highest version matching the prefix</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>The matched instance and data model, or null if not found</returns>
    public async Task<InstanceAndDataModel?> FindActiveDataAsync(string key, string version,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();

        // If full version → exact match (optimized query)
        if (InstanceDataVersionComparer.IsFullVersion(version))
        {
            return await (from instance in context.Instances
                          where instance.Status == InstanceStatus.Active
                          join data in context.InstancesData on instance.Id equals data.InstanceId
                          where instance.Key == key && data.Version == version
                          select new InstanceAndDataModel
                          {
                              Instance = instance,
                              InstanceData = data
                          })
                .AsNoTracking()
                .AsSplitQuery()
                .FirstOrDefaultAsync(cancellationToken);
        }

        // For artifact or partial version → load all matching versions and use smart matching
        var candidates = await (from instance in context.Instances
                                where instance.Status == InstanceStatus.Active && instance.Key == key
                                join data in context.InstancesData on instance.Id equals data.InstanceId
                                select new InstanceAndDataModel
                                {
                                    Instance = instance,
                                    InstanceData = data
                                })
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return null;

        // Use smart version matching
        var versions = candidates.Select(c => c.InstanceData.Version).ToList();
        var bestMatchVersion = InstanceDataVersionComparer.FindBestMatch(versions, version);

        if (string.IsNullOrEmpty(bestMatchVersion))
            return null;

        return candidates.FirstOrDefault(c =>
            string.Equals(c.InstanceData.Version, bestMatchVersion, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<InstanceAndDataModel>> GetActiveDataListByKeyAsync(string key,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        return await (from instance in context.Instances
                      where instance.Status == InstanceStatus.Active && instance.Key == key
                      join data in context.InstancesData on instance.Id equals data.InstanceId
                      select new InstanceAndDataModel
                      {
                          Instance = instance,
                          InstanceData = data
                      })
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }

    private async Task<IQueryable<Instance>> GetFilteredQueryAsync(
        string? filter,
        CancellationToken cancellationToken = default)
    {
        // Apply PostgreSQL native JSON filters if provided
        if (!string.IsNullOrWhiteSpace(filter))
        {
            try
            {
                var filteredInstances = (await GetDbSetAsync())
                    .ApplyFilters(
                        filter,
                        jsonColumnName: "Data",
                        tableName: "InstancesData",
                        schema: currentSchema.Name ?? "public",
                        schemaValidator: schemaValidator
                    );

                return filteredInstances
                    .Include(i => i.DataList);
            }
            catch (ArgumentException)
            {
                var dbSet = await GetDbSetAsync();
                var query = dbSet
                    .Include(i => i.DataList);
                var filterSpec = new InstanceFilterSpecification(filter);
                return filterSpec.Apply(query);
            }
            catch (FormatException)
            {
                var dbSet = await GetDbSetAsync();
                var query = dbSet
                    .Include(i => i.DataList);
                var filterSpec = new InstanceFilterSpecification(filter);
                return filterSpec.Apply(query);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                var dbSet = await GetDbSetAsync();
                var query = dbSet
                    .Include(i => i.DataList);
                var filterSpec = new InstanceFilterSpecification(filter);
                return filterSpec.Apply(query);
            }
        }

        var standardDbSet = await GetDbSetAsync();
        return standardDbSet
            .Include(i => i.DataList);
    }

    public async Task<HateoasPagedList<Instance>> GetPagedResultsAsync(
        int page,
        int pageSize,
        string? filter,
        string? groupBy = null,
        string? aggregations = null,
        CancellationToken cancellationToken = default)
    {
        // If groupBy or aggregations are provided, use ApplyFilterWithAggregationsAsync
        if (!string.IsNullOrWhiteSpace(groupBy) || !string.IsNullOrWhiteSpace(aggregations))
        {
            var context = await GetDbContextAsync();
            var dbSet = await GetDbSetAsync();

            string? combinedFilter = null;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (FilterFormatDetector.DetectFormat(filter) == FilterFormat.GraphQL)
                {
                    if (GraphQLFilterParser.TryParseRequest(filter, out var parsedRequest) && parsedRequest?.Filter != null)
                    {
                        combinedFilter = JsonSerializer.Serialize(parsedRequest.Filter, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = false
                        });
                    }
                    else
                    {
                        var combinedNode = FilterFormatDetector.CombineFilters(filter);
                        if (combinedNode != null)
                        {
                            combinedFilter = JsonSerializer.Serialize(combinedNode, new JsonSerializerOptions
                            {
                                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                WriteIndented = false
                            });
                        }
                    }
                }
                else
                {
                    var legacyNode = FilterFormatDetector.ConvertLegacyToGraphQL(filter);
                    if (legacyNode != null)
                    {
                        combinedFilter = JsonSerializer.Serialize(legacyNode, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = false
                        });
                    }
                }
            }

            var response = await UnifiedFilterService.ApplyFilterWithAggregationsAsync(
                context,
                dbSet,
                combinedFilter,
                groupBy,
                aggregations,
                "Data",
                currentSchema.Name ?? "public",
                query => query.Include(i => i.DataList).AsSplitQuery(),
                schemaValidator,
                cancellationToken);

            // If response has groups or aggregations, return empty paged list
            // (groups and aggregations are handled separately in the response)
            if (response.Groups != null || response.Aggregations != null)
            {
                return new HateoasPagedList<Instance>(
                    new List<Instance>(),
                    page,
                    pageSize,
                    false);
            }

            // If response has data, convert to HateoasPagedList
            if (response.Data != null)
            {
                var totalCount = response.Data.Count;
                var skip = (page - 1) * pageSize;
                var pagedData = response.Data.Skip(skip).Take(pageSize).ToList();
                var hasNext = skip + pageSize < totalCount;

                return new HateoasPagedList<Instance>(pagedData, page, pageSize, hasNext);
            }

            // Fallback to empty list
            return new HateoasPagedList<Instance>(
                new List<Instance>(),
                page,
                pageSize,
                false);
        }

        // Normal flow without groupBy/aggregations
        // GetFilteredQueryAsync already includes DataList, no need to include again
        var query = await GetFilteredQueryAsync(filter, cancellationToken);

        // Manually materialize to ensure DataList is loaded
        var skipCount = (page - 1) * pageSize;
        var items = await query
            .Skip(skipCount)
            .Take(pageSize + 1) // Take one extra to check if there's a next page
            .ToListAsync(cancellationToken);

        var hasNextPage = items.Count > pageSize;
        if (hasNextPage)
        {
            items = items.Take(pageSize).ToList();
        }

        return new HateoasPagedList<Instance>(items, page, pageSize, hasNextPage);
    }

    public async Task<(HateoasPagedList<Instance> PagedList, List<GroupSummary>? Groups)> GetPagedResultsWithGroupsAsync(
        int page,
        int pageSize,
        string? filter,
        string? groupBy = null,
        string? aggregations = null,
        string? sort = null,
        CancellationToken cancellationToken = default)
    {
        // If groupBy is provided, use ApplyFilterWithAggregationsAsync
        if (!string.IsNullOrWhiteSpace(groupBy))
        {
            var context = await GetDbContextAsync();
            var dbSet = await GetDbSetAsync();

            string? combinedFilter = null;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (FilterFormatDetector.DetectFormat(filter) == FilterFormat.GraphQL)
                {
                    if (GraphQLFilterParser.TryParseRequest(filter, out var parsedRequest) && parsedRequest?.Filter != null)
                    {
                        combinedFilter = JsonSerializer.Serialize(parsedRequest.Filter, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = false
                        });
                    }
                    else
                    {
                        var combinedNode = FilterFormatDetector.CombineFilters(filter);
                        if (combinedNode != null)
                        {
                            combinedFilter = JsonSerializer.Serialize(combinedNode, new JsonSerializerOptions
                            {
                                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                WriteIndented = false
                            });
                        }
                    }
                }
                else
                {
                    var legacyNode = FilterFormatDetector.ConvertLegacyToGraphQL(filter);
                    if (legacyNode != null)
                    {
                        combinedFilter = JsonSerializer.Serialize(legacyNode, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = false
                        });
                    }
                }
            }

            var response = await UnifiedFilterService.ApplyFilterWithAggregationsAsync(
                context,
                dbSet,
                combinedFilter,
                groupBy,
                aggregations,
                "Data",
                currentSchema.Name ?? "public",
                query => query.Include(i => i.DataList).AsSplitQuery(),
                schemaValidator,
                cancellationToken);

            // Convert GroupByResponse to GroupSummary
            List<GroupSummary>? groups = null;
            if (response.Groups != null && response.Groups.Count > 0)
            {
                groups = new List<GroupSummary>();
                var groupByRequest = GraphQLFilterParser.ParseGroupBy(groupBy);
                var groupByFields = groupByRequest?.GetFields() ?? new List<string>();

                foreach (var group in response.Groups)
                {
                    var summary = new GroupSummary();

                    // Concatenate all groupBy field values for the name
                    // This preserves all grouping keys (e.g., "USD_pending" for currency and status)
                    if (groupByFields.Count > 0 && group.Keys.Count > 0)
                    {
                        var keyValues = new List<string>();
                        foreach (var field in groupByFields)
                        {
                            if (group.Keys.TryGetValue(field, out var keyValue) && keyValue != null)
                            {
                                keyValues.Add(keyValue.ToString() ?? string.Empty);
                            }
                        }
                        summary.Name = string.Join("_", keyValues);
                    }

                    // Map aggregations
                    if (group.Aggregations != null)
                    {
                        summary.Count = group.Aggregations.Count;
                        summary.Sum = group.Aggregations.Sum;
                        summary.Avg = group.Aggregations.Avg;
                        summary.Min = group.Aggregations.Min;
                        summary.Max = group.Aggregations.Max;
                    }

                    groups.Add(summary);
                }
            }

            // Return empty paged list with groups
            return (new HateoasPagedList<Instance>(
                new List<Instance>(),
                page,
                pageSize,
                false), groups);
        }

        // If only aggregations (no groupBy), return empty groups
        if (!string.IsNullOrWhiteSpace(aggregations))
        {
            var context = await GetDbContextAsync();
            var dbSet = await GetDbSetAsync();

            string? combinedFilter = null;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (FilterFormatDetector.DetectFormat(filter) == FilterFormat.GraphQL)
                {
                    var combinedNode = FilterFormatDetector.CombineFilters(filter);
                    if (combinedNode != null)
                    {
                        combinedFilter = JsonSerializer.Serialize(combinedNode, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = false
                        });
                    }
                }
                else
                {
                    combinedFilter = filter;
                }
            }

            var response = await UnifiedFilterService.ApplyFilterWithAggregationsAsync(
                context,
                dbSet,
                combinedFilter,
                null, // no groupBy
                aggregations,
                "Data",
                currentSchema.Name ?? "public",
                query => query.Include(i => i.DataList).AsSplitQuery(),
                schemaValidator,
                cancellationToken);

            // Aggregations without groupBy - return empty groups
            HateoasPagedList<Instance> pagedList;
            if (response.Data != null)
            {
                var totalCount = response.Data.Count;
                var skip = (page - 1) * pageSize;
                var pagedData = response.Data.Skip(skip).Take(pageSize).ToList();
                var hasNext = skip + pageSize < totalCount;

                pagedList = new HateoasPagedList<Instance>(pagedData, page, pageSize, hasNext);
            }
            else
            {
                pagedList = new HateoasPagedList<Instance>(
                    new List<Instance>(),
                    page,
                    pageSize,
                    false);
            }

            return (pagedList, null);
        }

        // Normal flow without groupBy/aggregations
        var orderBy = GraphQLFilterParser.ParseOrderBy(sort);
        var hasAttributesOrderBy = orderBy != null && orderBy.GetEntries().Any(e => e.Field.Trim().StartsWith("attributes.", StringComparison.OrdinalIgnoreCase));
        var schema = currentSchema.Name ?? "public";

        var skipCount = (page - 1) * pageSize;
        List<Instance> items;
        bool hasNextPage;

        if (string.IsNullOrWhiteSpace(filter) && hasAttributesOrderBy)
        {
            // No filter + attributes orderBy: raw SQL with ORDER BY, then load DataList by IDs
            var orderByClause = GraphQLJsonFilterService.BuildOrderByClause(orderBy, schema);
            if (!string.IsNullOrEmpty(orderByClause))
            {
                var dbSet = await GetDbSetAsync();
                var rawSql = $"SELECT s.* FROM \"{schema}\".\"Instances\" s ORDER BY {orderByClause} OFFSET {skipCount} LIMIT {pageSize + 1}";
                var orderedInstances = await dbSet
                    .FromSqlRaw(rawSql)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);

                hasNextPage = orderedInstances.Count > pageSize;
                if (hasNextPage)
                    orderedInstances = orderedInstances.Take(pageSize).ToList();

                items = await LoadDataListAndPreserveOrderAsync(orderedInstances, cancellationToken);
            }
            else
            {
                var query = await GetFilteredQueryAsync(filter, cancellationToken);
                if (orderBy != null)
                    query = InstanceOrderByApplicator.Apply(query, orderBy);
                items = await query
                    .Skip(skipCount)
                    .Take(pageSize + 1)
                    .ToListAsync(cancellationToken);
                hasNextPage = items.Count > pageSize;
                if (hasNextPage)
                    items = items.Take(pageSize).ToList();
            }
        }
        else if (!string.IsNullOrWhiteSpace(filter) && hasAttributesOrderBy)
        {
            var combinedFilter = BuildCombinedFilterJson(filter);
            var orderByClause = GraphQLJsonFilterService.BuildOrderByClause(orderBy, schema);
            if (!string.IsNullOrEmpty(combinedFilter) && !string.IsNullOrEmpty(orderByClause))
            {
                var dbSet = await GetDbSetAsync();
                var filterNode = GraphQLFilterParser.ParseFilter(combinedFilter);
                if (filterNode != null && filterNode.NodeType != FilterNodeType.Empty)
                {
                    var query = dbSet.ApplyGraphQLFilter(filterNode, "Data", "InstancesData", schema, schemaValidator, null, orderByClause);
                    var orderedInstances = await query
                        .Skip(skipCount)
                        .Take(pageSize + 1)
                        .ToListAsync(cancellationToken);

                    hasNextPage = orderedInstances.Count > pageSize;
                    if (hasNextPage)
                        orderedInstances = orderedInstances.Take(pageSize).ToList();

                    items = await LoadDataListAndPreserveOrderAsync(orderedInstances, cancellationToken);
                }
                else
                {
                    var query = await GetFilteredQueryAsync(filter, cancellationToken);
                    if (orderBy != null)
                        query = InstanceOrderByApplicator.Apply(query, orderBy);
                    items = await query
                        .Skip(skipCount)
                        .Take(pageSize + 1)
                        .ToListAsync(cancellationToken);
                    hasNextPage = items.Count > pageSize;
                    if (hasNextPage)
                        items = items.Take(pageSize).ToList();
                }
            }
            else
            {
                var query = await GetFilteredQueryAsync(filter, cancellationToken);
                if (orderBy != null)
                    query = InstanceOrderByApplicator.Apply(query, orderBy);
                items = await query
                    .Skip(skipCount)
                    .Take(pageSize + 1)
                    .ToListAsync(cancellationToken);
                hasNextPage = items.Count > pageSize;
                if (hasNextPage)
                    items = items.Take(pageSize).ToList();
            }
        }
        else
        {
            var query = await GetFilteredQueryAsync(filter, cancellationToken);
            if (orderBy != null)
                query = InstanceOrderByApplicator.Apply(query, orderBy);
            items = await query
                .Skip(skipCount)
                .Take(pageSize + 1)
                .ToListAsync(cancellationToken);
            hasNextPage = items.Count > pageSize;
            if (hasNextPage)
                items = items.Take(pageSize).ToList();
        }

        var normalPagedList = new HateoasPagedList<Instance>(items, page, pageSize, hasNextPage);
        return (normalPagedList, null);
    }

    /// <summary>
    /// Gets paged results with optional groups using parsed GraphQL filter request (optimized - avoids parse-serialize cycle)
    /// </summary>
    public async Task<(HateoasPagedList<Instance> PagedList, List<GroupSummary>? Groups)> GetPagedResultsWithGroupsAsync(
        int page,
        int pageSize,
        Definitions.GraphQL.GraphQLFilterRequest? request,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        var dbSet = await GetDbSetAsync();

        var schema = currentSchema.Name ?? "public";
        var response = await UnifiedFilterService.ExecuteRequestAsync(
            context,
            dbSet,
            request ?? new Definitions.GraphQL.GraphQLFilterRequest(),
            "Data",
            schema,
            query => query.Include(i => i.DataList).AsSplitQuery(),
            applyOrderBy: (q, orderBy) => InstanceOrderByApplicator.Apply((IQueryable<Instance>)q, orderBy),
            applyOrderByRaw: (ctx, sch, orderBy) =>
            {
                if (orderBy == null) return ((WorkflowDbContext)ctx).Instances.AsQueryable();
                var clause = GraphQLJsonFilterService.BuildOrderByClause(orderBy, sch);
                if (string.IsNullOrEmpty(clause)) return ((WorkflowDbContext)ctx).Instances.AsQueryable();
                return ((WorkflowDbContext)ctx).Instances
                    .FromSqlRaw($"SELECT s.* FROM \"{sch}\".\"Instances\" s ORDER BY {clause}")
                    .AsNoTracking();
            },
            schemaValidator,
            cancellationToken);

        // Handle GroupBy response
        if (response.Groups != null && response.Groups.Count > 0)
        {
            var groups = new List<GroupSummary>();
            var groupByFields = request?.GroupBy?.GetFields() ?? new List<string>();

            foreach (var group in response.Groups)
            {
                var summary = new GroupSummary();

                // Concatenate all groupBy field values for the name
                if (groupByFields.Count > 0 && group.Keys.Count > 0)
                {
                    var keyValues = new List<string>();
                    foreach (var field in groupByFields)
                    {
                        if (group.Keys.TryGetValue(field, out var keyValue) && keyValue != null)
                        {
                            keyValues.Add(keyValue.ToString() ?? string.Empty);
                        }
                    }
                    summary.Name = string.Join("_", keyValues);
                }

                // Map aggregations
                if (group.Aggregations != null)
                {
                    summary.Count = group.Aggregations.Count;
                    summary.Sum = group.Aggregations.Sum;
                    summary.Avg = group.Aggregations.Avg;
                    summary.Min = group.Aggregations.Min;
                    summary.Max = group.Aggregations.Max;
                }

                groups.Add(summary);
            }

            return (new HateoasPagedList<Instance>(
                new List<Instance>(),
                page,
                pageSize,
                false), groups);
        }

        // Handle aggregations without groupBy
        if (response.Aggregations != null)
        {
            HateoasPagedList<Instance> pagedList;
            if (response.Data != null)
            {
                var totalCount = response.Data.Count;
                var skip = (page - 1) * pageSize;
                var pagedData = response.Data.Skip(skip).Take(pageSize).ToList();
                var hasNext = skip + pageSize < totalCount;

                pagedList = new HateoasPagedList<Instance>(pagedData, page, pageSize, hasNext);
            }
            else
            {
                pagedList = new HateoasPagedList<Instance>(
                    new List<Instance>(),
                    page,
                    pageSize,
                    false);
            }

            return (pagedList, null);
        }

        // Handle regular filter (no aggregations)
        HateoasPagedList<Instance> resultPagedList;
        if (response.Data != null)
        {
            var totalCount = response.Data.Count;
            var skip = (page - 1) * pageSize;
            var pagedData = response.Data.Skip(skip).Take(pageSize).ToList();
            var hasNext = skip + pageSize < totalCount;

            resultPagedList = new HateoasPagedList<Instance>(pagedData, page, pageSize, hasNext);
        }
        else
        {
            resultPagedList = new HateoasPagedList<Instance>(
                new List<Instance>(),
                page,
                pageSize,
                false);
        }

        return (resultPagedList, null);
    }


    public async Task<Result<Instance>> GetActiveAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var instanceResult = await GetResultAsync(identifier, includeDetails: true, cancellationToken);

        if (!instanceResult.IsSuccess)
        {
            return instanceResult;
        }

        var instance = instanceResult.Value!;

        if (instance.IsCompleted)
        {
            return Result<Instance>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceCompleted,
                $"Instance {identifier} is already completed with status: {instance.Status.Code}",
                identifier));
        }

        return Result<Instance>.Ok(instance);
    }

    /// <summary>
    /// Gets an instance by ID using Result pattern.
    /// Returns Result.NotFound if instance doesn't exist.
    /// </summary>
    public async Task<Result<Instance>> GetResultAsync(string identifier, bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var instance = await FindByIdentifierAsync(identifier, cancellationToken);

        if (instance is null)
        {
            return Result<Instance>.Fail(Error.NotFound(
                WorkflowErrorCodes.InstanceNotFound,
                $"Instance with ID {identifier} not found",
                identifier));
        }

        return Result<Instance>.Ok(instance);
    }

    public async Task<List<InstanceAndDataModel>> GetActiveDataListAsync(CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();

        // Optimize query with proper indexing and reduced data transfer
        return await (from instance in context.Instances
                      where instance.Status == InstanceStatus.Active
                      join data in context.InstancesData on instance.Id equals data.InstanceId
                      select new InstanceAndDataModel
                      {
                          Instance = instance,
                          InstanceData = data
                      })
            .AsNoTracking() // Don't track changes for read-only operations
            .AsSplitQuery() // Use split queries for better performance with joins
            .ToListAsync(cancellationToken);
    }

    public async Task<List<InstanceAndDataModel>> GetActiveDataListPagedAsync(
        int skip, int take, CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();

        return await (from instance in context.Instances
                      where instance.Status == InstanceStatus.Active
                      orderby instance.Id
                      join data in context.InstancesData on instance.Id equals data.InstanceId
                      select new InstanceAndDataModel
                      {
                          Instance = instance,
                          InstanceData = data
                      })
            .AsNoTracking()
            .AsSplitQuery()
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<InstanceAndDataModel>> GetActiveDataListSinceAsync(
        DateTime since, int skip, int take, CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();

        // Uses the LastTouchedAt STORED GENERATED column (COALESCE(ModifiedAt, CreatedAt)).
        // The previous (instance.ModifiedAt ?? instance.CreatedAt) expression prevented the
        // planner from using any index. Reading the shadow column via EF.Property keeps the
        // query strongly typed while letting IX_Instances_Active_LastTouched_Id serve it.
        return await (from instance in context.Instances
                      where instance.Status == InstanceStatus.Active
                            && EF.Property<DateTime>(instance, "LastTouchedAt") >= since
                      orderby EF.Property<DateTime>(instance, "LastTouchedAt"), instance.Id
                      join data in context.InstancesData on instance.Id equals data.InstanceId
                      select new InstanceAndDataModel
                      {
                          Instance = instance,
                          InstanceData = data
                      })
            .AsNoTracking()
            .AsSplitQuery()
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AnyActiveByKeyAsync(string key, Guid excludeInstanceId,
        CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .AsNoTracking()
            .AnyAsync(
                i => i.Key == key
                     && i.Id != excludeInstanceId
                     && i.Status == InstanceStatus.Active,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<InstanceKeyModel>> GetActiveInstanceKeysAsync(CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        return await (from instance in context.Instances
                      where instance.Status == InstanceStatus.Active
                      join data in context.InstancesData on instance.Id equals data.InstanceId
                      where data.IsLatest
                      select new InstanceKeyModel(instance.Key!, data.Version))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Loads DataList for instances (ordered by id list) and returns list in the same order. Used when ORDER BY must be preserved (EF Include breaks order).
    /// </summary>
    private async Task<List<Instance>> LoadDataListAndPreserveOrderAsync(List<Instance> orderedInstances, CancellationToken cancellationToken)
    {
        if (orderedInstances.Count == 0)
            return [];
        var ids = orderedInstances.Select(i => i.Id).ToList();
        var dbSet = await GetDbSetAsync();
        var instancesWithData = await dbSet
            .Where(i => ids.Contains(i.Id))
            .Include(i => i.DataList)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var byId = instancesWithData.ToDictionary(i => i.Id);
        return ids.Select(id => byId[id]).ToList();
    }

    /// <summary>
    /// Builds a single GraphQL filter JSON from the filter string (same logic as groupBy/aggregations path).
    /// </summary>
    private static string? BuildCombinedFilterJson(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return null;
        if (FilterFormatDetector.DetectFormat(filter) == FilterFormat.GraphQL)
        {
            if (GraphQLFilterParser.TryParseRequest(filter, out var parsedRequest) && parsedRequest?.Filter != null)
            {
                return JsonSerializer.Serialize(parsedRequest.Filter, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                });
            }
            var combinedNode = FilterFormatDetector.CombineFilters(filter);
            if (combinedNode != null)
            {
                return JsonSerializer.Serialize(combinedNode, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                });
            }
        }
        else
        {
            var legacyNode = FilterFormatDetector.ConvertLegacyToGraphQL(filter);
            if (legacyNode != null)
            {
                return JsonSerializer.Serialize(legacyNode, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                });
            }
        }
        return null;
    }

    /// <summary>
    /// Handles metrics recording for instance status changes
    /// </summary>
    private async Task HandleStatusChangeMetrics(Instance entity, InstanceStatus oldStatus, InstanceStatus newStatus)
    {
        // Update status transition metrics (handles all status gauge changes)
        workflowMetrics.UpdateInstanceStatusMetrics(entity.Flow, oldStatus.Code, newStatus.Code);

        // Record specific completion events with duration
        if (newStatus.Equals(InstanceStatus.Completed))
        {
            var durationSeconds = entity.Duration?.TotalSeconds;
            workflowMetrics.RecordInstanceCompleted(entity.Flow, runtimeInfoProvider.Domain, durationSeconds);
        }

        // Record specific error events with duration
        if (newStatus.Equals(InstanceStatus.Faulted))
        {
            var durationSeconds = entity.Duration?.TotalSeconds;
            workflowMetrics.RecordError("instance_faulted", "High", "Instance");

            // Record duration for faulted instances
            if (durationSeconds.HasValue)
            {
                workflowMetrics.RecordInstanceDuration(entity.Flow, "Faulted", durationSeconds.Value);
            }
        }

        await Task.CompletedTask; // For potential future async operations
    }
}