using System.Text.Json;
using BBT.Aether;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Filtering;
using BBT.Workflow.Infrastructure.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using BBT.Workflow.BackgroundJobs.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BBT.Workflow.Definitions.Schemas;
namespace BBT.Workflow.Instances;

public sealed class EfCoreInstanceRepository(
    IAetherDbContextProvider<WorkflowDbContext> dbContext,
    IServiceProvider serviceProvider,
    IWorkflowMetrics workflowMetrics,
    IRuntimeInfoProvider runtimeInfoProvider,
    IDataSinkManager dataSinkManager,
     ICurrentSchema currentSchema,
    ISchemaValidator schemaValidator,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<EfCoreInstanceRepository> logger)
    : EfCoreRepository<WorkflowDbContext, Instance, Guid>(dbContext, serviceProvider),
        IInstanceRepository
{
    private const string DefaultSchemaName = "public";

    // Cached and reused: JsonSerializerOptions caches serialization metadata internally, so a fresh
    // instance per call would rebuild that metadata every time (CA1869). All wire-filter
    // serialization in this repository uses the same camelCase + compact shape.
    private static readonly JsonSerializerOptions CamelCaseCompactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public override async Task<IQueryable<Instance>> WithDetailsAsync()
    {
        // Default (legacy) load pulls the full InstanceData history. With latest-only loading
        // enabled (WorkflowExecution:LatestOnlyInstanceLoading), only the IsLatest row is
        // included: the full-merge model makes it self-sufficient for pipeline merges, script
        // context and polling (it carries the complete state, the highest version and the
        // highest HistorySequence of its own version line). History-dependent callers must use
        // FindByIdentifierWithFullHistoryAsync / FindByIdentifierWithFullDataAsync — aggregates
        // materialized through the identifier finders are marked partially loaded and fail fast
        // on history reads. ChildCorrelations are always filtered to active-only.
        var query = await base.WithDetailsAsync();

        query = LatestOnlyLoading
            ? query.Include(i => i.DataList.Where(d => d.IsLatest))
            : query.Include(i => i.DataList);

        return query.Include(i => i.ChildCorrelations.Where(c => !c.IsCompleted));
    }

    private bool LatestOnlyLoading => executionOptions.Value.LatestOnlyInstanceLoading;

    /// <summary>
    /// Conditional data include for list/query paths: list consumers read only the latest
    /// version (full-merge model), so latest-only loading spares each listed instance its
    /// entire version history. History-reading flows use the dedicated full-history APIs.
    /// </summary>
    private IQueryable<Instance> IncludeListData(IQueryable<Instance> query) =>
        LatestOnlyLoading
            ? query.Include(i => i.DataList.Where(d => d.IsLatest))
            : query.Include(i => i.DataList);

    /// <summary>
    /// Stamps the latest-only marker on a materialized aggregate so history-dependent domain
    /// members fail fast instead of silently answering from a partial list.
    /// </summary>
    private Instance? MarkIfPartiallyLoaded(Instance? instance)
    {
        if (instance is not null && LatestOnlyLoading)
        {
            instance.MarkDataPartiallyLoaded();
        }

        return instance;
    }

    /// <summary>
    /// Stamps the latest-only marker on every instance in a list/paged result so a consumer
    /// (view/extension/script) that reaches for <c>DataList</c> / <c>FindData</c> /
    /// <c>GetVersionHistory</c> fails fast instead of reading a silently-incomplete history.
    /// No-op when latest-only loading is off. Fluent: returns the same list for inline use.
    /// </summary>
    private List<Instance> MarkListIfPartiallyLoaded(List<Instance> instances)
    {
        if (LatestOnlyLoading)
        {
            foreach (var instance in instances)
            {
                instance.MarkDataPartiallyLoaded();
            }
        }

        return instances;
    }

    /// <inheritdoc />
    public async Task<Instance?> FindByFilterAsync(
        InstanceFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Schema resolution can be influenced by request input (headers/route); strip quote
        // characters before interpolating into the quoted identifier, like the other raw-SQL sites.
        var schema = SanitizeIdentifier(currentSchema.Name ?? DefaultSchemaName);
        var builder = new InstanceFilterSqlBuilder();
        var whereClause = builder.BuildWhere(filter.Root);

        // First = base order; Last = base order reversed. Then take the top row.
        var effectiveDescending = filter.Selection == InstanceSelection.First
            ? filter.Order.Descending
            : !filter.Order.Descending;
        var orderClause = InstanceFilterSqlBuilder.BuildOrderBy(filter.Order.Field, effectiveDescending);

        // Join each instance to its latest data row so attribute (JSONB) predicates and instance-column
        // predicates can be evaluated together. SELECT s.* keeps the result mappable to Instance.
        var sql =
            $"SELECT s.* FROM \"{schema}\".\"Instances\" s " +
            $"LEFT JOIN \"{schema}\".\"InstancesData\" d ON d.\"InstanceId\" = s.\"Id\" AND d.\"IsLatest\" = true " +
            $"WHERE {whereClause} ORDER BY {orderClause} LIMIT 1";

        var dbSet = await GetDbSetAsync();
        return await dbSet
            .FromSqlRaw(sql, builder.Parameters.ToArray())
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Instance?> FindWithActiveSubFlowAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Include(i => i.ChildCorrelations
                .Where(c => !c.IsCompleted && c.SubFlowType == SubFlowType.SubFlow))
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Instance?> FindWithAllCorrelationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Include(i => i.ChildCorrelations)
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
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
    /// Updates an instance and automatically records status change metrics.
    /// Uses EF change tracker to detect status changes without an extra DB round-trip.
    /// </summary>
    public override async Task<Instance> UpdateAsync(Instance entity, bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await GetDbContextAsync();
        var entry = dbContext.Entry(entity);

        InstanceStatus? originalStatus = null;
        if (entry.State != EntityState.Detached)
        {
            var statusProperty = entry.Property(nameof(Instance.Status));
            if (statusProperty.IsModified)
            {
                originalStatus = (InstanceStatus)statusProperty.OriginalValue!;
            }
        }

        var result = await base.UpdateAsync(entity, autoSave, cancellationToken);

        if (originalStatus != null && !originalStatus.Equals(entity.Status))
        {
            await HandleStatusChangeMetrics(entity, originalStatus, entity.Status);
        }

        try
        {
            await dataSinkManager.HandleUpdateAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
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
                return MarkIfPartiallyLoaded(response);
            }
        }

        // Key is not unique across terminal/historical rows; OrderByDescending(CreatedAt)
        // keeps the fallback deterministic by returning the most recent instance for the key.
        return MarkIfPartiallyLoaded(await query
            .Where(p => p.Key == identifier)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async Task<Instance?> FindActiveByKeyAsync(string key,
        CancellationToken cancellationToken = default)
    {
        var query = (await WithDetailsAsync())
            .AsSplitQuery();

        // Only non-terminal instances occupy a key (Active or Busy). Terminal rows
        // (Completed/Faulted/Passive) are ignored. OrderByDescending(CreatedAt) keeps the
        // result deterministic even if legacy data left more than one live row for a key.
        return MarkIfPartiallyLoaded(await query
            .Where(i => i.Key == key
                        && (i.Status == InstanceStatus.Active || i.Status == InstanceStatus.Busy))
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken));
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
                return MarkIfPartiallyLoaded(response);
            }
        }

        // Key is not unique across terminal/historical rows; OrderByDescending(CreatedAt)
        // keeps the fallback deterministic by returning the most recent instance for the key.
        return MarkIfPartiallyLoaded(await query
            .Where(p => p.Key == identifier)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async Task<Instance?> FindByIdentifierSlimAsync(
        string identifier,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        var query = dbSet
            .Include(i => i.ChildCorrelations.Where(c => !c.IsCompleted))
            .AsNoTracking()
            .AsSplitQuery();

        if (Guid.TryParse(identifier, out var instanceId))
        {
            var response = await query
                .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
            if (response != null)
                return response;
        }

        return await query
            .FirstOrDefaultAsync(i => i.Key == identifier, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Instance?> FindByIdentifierWithFullHistoryAsync(string identifier,
        CancellationToken cancellationToken = default)
    {
        // Read-only query for history enumeration. Uses AsNoTracking since the caller
        // only reads; WithDetailsAsync is not used here to keep the query detached.
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

        // Key is not unique across terminal/historical rows; OrderByDescending(CreatedAt)
        // keeps the fallback deterministic by returning the most recent instance for the key.
        return await query
            .Where(p => p.Key == identifier)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
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

        // Key is not unique across terminal/historical rows; OrderByDescending(CreatedAt)
        // keeps the fallback deterministic by returning the most recent instance for the key.
        return await query
            .Where(p => p.Key == identifier)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
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

        // If full version → exact match
        if (InstanceDataVersionComparer.IsFullVersion(version))
        {
            return await context.Instances
                .Where(i => i.Status == InstanceStatus.Active && i.Key == key)
                .Join(context.InstancesData,
                    i => i.Id,
                    d => d.InstanceId,
                    (i, d) => new { Instance = i, Data = d })
                .Where(x => x.Data.Version == version)
                .Select(x => new InstanceAndDataModel
                {
                    Instance = x.Instance,
                    InstanceData = x.Data
                })
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        // For artifact or partial version → two-phase resolution: SQL narrows, the canonical
        // comparer decides. Phase 1 projects only the version strings (no jsonb payloads —
        // under the full-merge model every historical payload is a complete state copy, so
        // materializing all candidates was the real cost). Phase 2 fetches exactly the one
        // winning row. FindBestMatch stays the single source of truth for version semantics.
        var candidateVersions = await context.Instances
            .Where(i => i.Status == InstanceStatus.Active && i.Key == key)
            .Join(context.InstancesData,
                i => i.Id,
                d => d.InstanceId,
                (i, d) => d.Version)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (candidateVersions.Count == 0)
            return null;

        var bestMatchVersion = InstanceDataVersionComparer.FindBestMatch(candidateVersions, version);

        if (string.IsNullOrEmpty(bestMatchVersion))
            return null;

        return await context.Instances
            .Where(i => i.Status == InstanceStatus.Active && i.Key == key)
            .Join(context.InstancesData,
                i => i.Id,
                d => d.InstanceId,
                (i, d) => new { Instance = i, Data = d })
            .Where(x => x.Data.Version == bestMatchVersion)
            .Select(x => new InstanceAndDataModel
            {
                Instance = x.Instance,
                InstanceData = x.Data
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<InstanceAndDataModel>> GetActiveDataListByKeyAsync(string key,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        return await context.Instances
            .Where(i => i.Status == InstanceStatus.Active && i.Key == key)
            .Join(context.InstancesData,
                i => i.Id,
                d => d.InstanceId,
                (i, d) => new InstanceAndDataModel
                {
                    Instance = i,
                    InstanceData = d
                })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private async Task<IQueryable<Instance>> GetFilteredQueryAsync(
        string? filter,
        SchemaFilterContext? schemaContext = null,
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
                        schema: currentSchema.Name ?? DefaultSchemaName,
                        schemaValidator: schemaValidator,
                        schemaContext: schemaContext
                    );

                return IncludeListData(filteredInstances);
            }
            catch (ArgumentException)
            {
                var dbSet = await GetDbSetAsync();
                var query = IncludeListData(dbSet);
                var filterSpec = new InstanceFilterSpecification(filter);
                return filterSpec.Apply(query);
            }
            catch (FormatException)
            {
                var dbSet = await GetDbSetAsync();
                var query = IncludeListData(dbSet);
                var filterSpec = new InstanceFilterSpecification(filter);
                return filterSpec.Apply(query);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                var dbSet = await GetDbSetAsync();
                var query = IncludeListData(dbSet);
                var filterSpec = new InstanceFilterSpecification(filter);
                return filterSpec.Apply(query);
            }
        }

        var standardDbSet = await GetDbSetAsync();
        return IncludeListData(standardDbSet);
    }

    public async Task<HateoasPagedList<Instance>> GetPagedResultsAsync(
        int page,
        int pageSize,
        string? filter,
        string? groupBy = null,
        string? aggregations = null,
        CancellationToken cancellationToken = default,
        SchemaFilterContext? schemaContext = null)
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
                        combinedFilter = JsonSerializer.Serialize(parsedRequest.Filter, CamelCaseCompactJson);
                    }
                    else
                    {
                        var combinedNode = FilterFormatDetector.CombineFilters(filter);
                        if (combinedNode != null)
                        {
                            combinedFilter = JsonSerializer.Serialize(combinedNode, CamelCaseCompactJson);
                        }
                    }
                }
                else
                {
                    var legacyNode = FilterFormatDetector.ConvertLegacyToGraphQL(filter);
                    if (legacyNode != null)
                    {
                        combinedFilter = JsonSerializer.Serialize(legacyNode, CamelCaseCompactJson);
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
                currentSchema.Name ?? DefaultSchemaName,
                query => IncludeListData(query).AsSplitQuery(),
                schemaValidator,
                cancellationToken,
                schemaContext);

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

                return new HateoasPagedList<Instance>(MarkListIfPartiallyLoaded(pagedData), page, pageSize, hasNext);
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
        var query = await GetFilteredQueryAsync(filter, null, cancellationToken);

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

        return new HateoasPagedList<Instance>(MarkListIfPartiallyLoaded(items), page, pageSize, hasNextPage);
    }

    public async Task<(HateoasPagedList<Instance> PagedList, List<GroupSummary>? Groups)> GetPagedResultsWithGroupsAsync(
        int page,
        int pageSize,
        string? filter,
        string? groupBy = null,
        string? aggregations = null,
        string? sort = null,
        SchemaFilterContext? schemaContext = null,
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
                        combinedFilter = JsonSerializer.Serialize(parsedRequest.Filter, CamelCaseCompactJson);
                    }
                    else
                    {
                        var combinedNode = FilterFormatDetector.CombineFilters(filter);
                        if (combinedNode != null)
                        {
                            combinedFilter = JsonSerializer.Serialize(combinedNode, CamelCaseCompactJson);
                        }
                    }
                }
                else
                {
                    var legacyNode = FilterFormatDetector.ConvertLegacyToGraphQL(filter);
                    if (legacyNode != null)
                    {
                        combinedFilter = JsonSerializer.Serialize(legacyNode, CamelCaseCompactJson);
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
                currentSchema.Name ?? DefaultSchemaName,
                query => IncludeListData(query).AsSplitQuery(),
                schemaValidator,
                cancellationToken,
                schemaContext);

            // Convert GroupByResponse to GroupSummary
            List<GroupSummary>? groups = null;
            if (response.Groups is { Count: > 0 })
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
                    if (group.Keys.Count > 0)
                        summary.Keys = new Dictionary<string, object?>(group.Keys);
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
                        combinedFilter = JsonSerializer.Serialize(combinedNode, CamelCaseCompactJson);
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
                currentSchema.Name ?? DefaultSchemaName,
                query => IncludeListData(query).AsSplitQuery(),
                schemaValidator,
                cancellationToken,
                schemaContext);

            // Aggregations without groupBy - return empty groups
            HateoasPagedList<Instance> pagedList;
            if (response.Data != null)
            {
                var totalCount = response.Data.Count;
                var skip = (page - 1) * pageSize;
                var pagedData = response.Data.Skip(skip).Take(pageSize).ToList();
                var hasNext = skip + pageSize < totalCount;

                pagedList = new HateoasPagedList<Instance>(MarkListIfPartiallyLoaded(pagedData), page, pageSize, hasNext);
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
        var schema = currentSchema.Name ?? DefaultSchemaName;

        var skipCount = (page - 1) * pageSize;
        List<Instance> items;
        bool hasNextPage;

        if (string.IsNullOrWhiteSpace(filter) && hasAttributesOrderBy)
        {
            var orderByClause = GraphQLJsonFilterService.BuildOrderByClause(orderBy, schema, schemaContext: schemaContext);
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
                var query = await GetFilteredQueryAsync(filter, schemaContext, cancellationToken);
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
            var orderByClause = GraphQLJsonFilterService.BuildOrderByClause(orderBy, schema, schemaContext: schemaContext);
            if (!string.IsNullOrEmpty(combinedFilter) && !string.IsNullOrEmpty(orderByClause))
            {
                var dbSet = await GetDbSetAsync();
                var filterNode = GraphQLFilterParser.ParseFilter(combinedFilter);
                if (filterNode != null && filterNode.NodeType != FilterNodeType.Empty)
                {
                    var query = dbSet.ApplyGraphQLFilter(filterNode, "Data", "InstancesData", schema, schemaValidator, null, orderByClause, schemaContext: schemaContext);
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
                    var query = await GetFilteredQueryAsync(filter, schemaContext, cancellationToken);
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
                var query = await GetFilteredQueryAsync(filter, schemaContext, cancellationToken);
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
            var query = await GetFilteredQueryAsync(filter, schemaContext, cancellationToken);
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
 
        var normalPagedList = new HateoasPagedList<Instance>(MarkListIfPartiallyLoaded(items), page, pageSize, hasNextPage);
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

        var schema = currentSchema.Name ?? DefaultSchemaName;
        var response = await UnifiedFilterService.ExecuteRequestAsync(
            context,
            dbSet,
            request ?? new Definitions.GraphQL.GraphQLFilterRequest(),
            "Data",
            schema,
            query => IncludeListData(query).AsSplitQuery(),
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
        if (response.Groups is { Count: > 0 })
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
                if (group.Keys.Count > 0)
                    summary.Keys = new Dictionary<string, object?>(group.Keys);
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

                pagedList = new HateoasPagedList<Instance>(MarkListIfPartiallyLoaded(pagedData), page, pageSize, hasNext);
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

            resultPagedList = new HateoasPagedList<Instance>(MarkListIfPartiallyLoaded(pagedData), page, pageSize, hasNext);
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
        return await context.Instances
            .Where(i => i.Status == InstanceStatus.Active)
            .Join(context.InstancesData,
                i => i.Id,
                d => d.InstanceId,
                (i, d) => new InstanceAndDataModel
                {
                    Instance = i,
                    InstanceData = d
                })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<List<InstanceAndDataModel>> GetActiveDataListPagedAsync(
        int skip, int take, CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        return await context.Instances
            .Where(i => i.Status == InstanceStatus.Active)
            .OrderBy(i => i.Id)
            .Join(context.InstancesData,
                i => i.Id,
                d => d.InstanceId,
                (i, d) => new InstanceAndDataModel
                {
                    Instance = i,
                    InstanceData = d
                })
            .AsNoTracking()
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<ActiveInstanceDataSummary>> GetActiveDataSummariesPagedAsync(
        int skip, int take, CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        var raw = await context.Instances
            .Where(i => i.Status == InstanceStatus.Active)
            .OrderBy(i => i.Id)
            .Join(context.InstancesData,
                i => i.Id,
                d => d.InstanceId,
                (i, d) => new
                {
                    i.Key,
                    i.FlowVersion,
                    i.Tags,
                    i.CreatedAt,
                    i.ModifiedAt,
                    DataJson = d.Data.Json,
                    d.Version,
                    d.IsLatest
                })
            .AsNoTracking()
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return raw.Select(r => new ActiveInstanceDataSummary(
            r.Key,
            r.FlowVersion,
            r.Tags,
            r.CreatedAt,
            r.ModifiedAt,
            JsonSerializer.Deserialize<JsonElement>(r.DataJson, JsonSerializerConstants.JsonOptions),
            r.Version,
            r.IsLatest))
            .ToList();
    }

    public async Task<List<ComponentVersionSummary>> GetVersionsPagedAsync(
        string key, int skip, int take, CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        var raw = await context.Instances
            .Where(i => i.Status == InstanceStatus.Active && i.Key == key)
            .Join(context.InstancesData,
                i => i.Id,
                d => d.InstanceId,
                (i, d) => new
                {
                    d.Version,
                    d.IsLatest,
                    i.FlowVersion,
                    PublishedAt = d.EnteredAt
                })
            .OrderByDescending(x => x.IsLatest)
            .ThenByDescending(x => x.PublishedAt)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return raw.Select(r => new ComponentVersionSummary(r.Version, r.IsLatest, r.FlowVersion, r.PublishedAt))
                  .ToList();
    }

    public async Task<List<InstanceAndDataModel>> GetActiveDataListSinceAsync(
        DateTime since, int skip, int take, CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();

        // Uses the LastTouchedAt STORED GENERATED column (COALESCE(ModifiedAt, CreatedAt)).
        // Reading the shadow column via EF.Property keeps the query strongly typed
        // while letting IX_Instances_Active_LastTouched_Id serve it.
        return await context.Instances
            .Where(i => i.Status == InstanceStatus.Active
                        && EF.Property<DateTime>(i, "LastTouchedAt") >= since)
            .OrderBy(i => EF.Property<DateTime>(i, "LastTouchedAt"))
            .ThenBy(i => i.Id)
            .Join(context.InstancesData,
                i => i.Id,
                d => d.InstanceId,
                (i, d) => new InstanceAndDataModel
                {
                    Instance = i,
                    InstanceData = d
                })
            .AsNoTracking()
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AnyActiveByKeyAsync(string key, Guid excludeInstanceId,
        CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        return await context.Instances.AnyAsync(
            i => i.Key == key
                 && i.Id != excludeInstanceId
                 && (i.Status == InstanceStatus.Active || i.Status == InstanceStatus.Busy),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<InstanceKeyModel>> GetActiveInstanceKeysAsync(CancellationToken cancellationToken = default)
    {
        var context = await GetDbContextAsync();
        return await context.Instances
            .Where(i => i.Status == InstanceStatus.Active)
            .Join(context.InstancesData,
                i => i.Id,
                d => d.InstanceId,
                (i, d) => new { Instance = i, Data = d })
            .Where(x => x.Data.IsLatest)
            .Select(x => new InstanceKeyModel(x.Instance.Key!, x.Data.Version))
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
        var instancesWithData = await IncludeListData(
                dbSet.Where(i => ids.Contains(i.Id)))
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
                return JsonSerializer.Serialize(parsedRequest.Filter, CamelCaseCompactJson);
            }
            var combinedNode = FilterFormatDetector.CombineFilters(filter);
            if (combinedNode != null)
            {
                return JsonSerializer.Serialize(combinedNode, CamelCaseCompactJson);
            }
        }
        else
        {
            var legacyNode = FilterFormatDetector.ConvertLegacyToGraphQL(filter);
            if (legacyNode != null)
            {
                return JsonSerializer.Serialize(legacyNode, CamelCaseCompactJson);
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

    /// <inheritdoc />
    public async Task<List<Instance>> GetHumanTaskInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        var schema = SanitizeIdentifier(currentSchema.Name ?? string.Empty);
        var activeCode = InstanceStatus.Active.Code;
        var busyCode = InstanceStatus.Busy.Code;
        var subType = (int)StateSubType.Human;

        var dbSet = await GetDbSetAsync();

        return await IncludeListData(dbSet
                .FromSqlRaw(
                    "SELECT * FROM \"" + schema + "\".\"Instances\""
                    + " WHERE \"Status\" IN ({0}, {1})"
                    + " AND \"EffectiveStateSubType\" = {2}"
                    + " AND NOT (\"ExtraProperties\"::jsonb ? 'parent.id')"
                    + " ORDER BY \"CreatedAt\" DESC",
                    activeCode, busyCode, subType))
            .Include(i => i.ChildCorrelations)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Instance>> GetStuckBusyChainsAsync(
        DateTime olderThanUtc, int maxCount, CancellationToken cancellationToken = default)
    {
        var limit = maxCount <= 0 ? 100 : maxCount;

        var dbSet = await GetDbSetAsync();

        // Schema is resolved by the schema-aware DbContext from the ambient ICurrentSchema,
        // established by the caller (ChainReaperHostedService via IcurrentSchema.Change(flowKey)).
        // Tracked (no AsNoTracking): the reaper faults / updates the returned instances.
        return await dbSet
            .Where(i => i.Status == InstanceStatus.Busy
                && i.ChainToken != null
                && i.ChainHeartbeatAt != null
                && i.ChainHeartbeatAt < olderThanUtc)
            .OrderBy(i => i.ChainHeartbeatAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetActiveFlowKeysAsync(
        CancellationToken cancellationToken = default)
    {
        // Flow definitions are stored as instances in the sys_flows schema; switch to it for this
        // read only so a background sweep (no request scope) can enumerate the per-flow schemas.
        // Mirrors the discovery in SchemaMigrationRunner.
        using (currentSchema.Change(RuntimeSysSchemaInfo.Flows))
        {
            var dbSet = await GetDbSetAsync();
            return await dbSet
                .AsNoTracking()
                .Where(i => i.Key != null)
                .Select(i => i.Key!)
                .Distinct()
                .ToListAsync(cancellationToken);
        }
    }

    private static string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace("\"", "", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(
        string? filter,
        CancellationToken cancellationToken = default
    )
    {
        var query = await GetFilteredQueryAsync(filter, null, cancellationToken);
        return await query.LongCountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstanceStatusCounts> GetStatusCountsAsync(
        string? filter,
        CancellationToken cancellationToken = default)
    {
        // Single round-trip: COUNT(*) FILTER (WHERE "Status" = …) per status, over the filtered set.
        // Reuses GetFilteredQueryAsync so the optional GraphQL/legacy filter (e.g. createdAt range)
        // is applied identically to CountAsync. GroupBy(_ => 1) collapses to a whole-set aggregate;
        // the i.Status == InstanceStatus.X comparison is the same value-converted predicate CountByStatusAsync uses.
        var query = await GetFilteredQueryAsync(filter, null, cancellationToken);
        var counts = await query
            .GroupBy(_ => 1)
            .Select(g => new InstanceStatusCounts(
                g.LongCount(i => i.Status == InstanceStatus.Active),
                g.LongCount(i => i.Status == InstanceStatus.Busy),
                g.LongCount(i => i.Status == InstanceStatus.Completed),
                g.LongCount(i => i.Status == InstanceStatus.Faulted),
                g.LongCount(i => i.Status == InstanceStatus.Passive)))
            .FirstOrDefaultAsync(cancellationToken);

        return counts ?? new InstanceStatusCounts(0, 0, 0, 0, 0);
    }

    /// <inheritdoc />
    public async Task<long> CountByStatusAsync(
        InstanceStatus? status,
        string? flowVersion,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        var query = dbSet.AsNoTracking();
        if (status is not null)
            query = query.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(flowVersion))
            query = query.Where(i => i.FlowVersion == flowVersion);
        return await query.LongCountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> CountByStateAsync(
        string stateKey,
        InstanceStatus? status,
        string? flowVersion,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        var query = dbSet.AsNoTracking().Where(i => i.CurrentState == stateKey);
        if (status is not null)
            query = query.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(flowVersion))
            query = query.Where(i => i.FlowVersion == flowVersion);
        return await query.LongCountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstanceDurationStat> GetDurationStatAsync(CancellationToken cancellationToken = default)
    {
        var schema = currentSchema.Name ?? DefaultSchemaName;
        var context = await GetDbContextAsync();
        var result = await context.Database
            .SqlQueryRaw<InstanceDurationRaw>(
                $"SELECT COALESCE(AVG(EXTRACT(EPOCH FROM \"Duration\") * 1000), 0) AS \"AvgMs\", " +
                $"COALESCE(MIN(EXTRACT(EPOCH FROM \"Duration\") * 1000), 0) AS \"MinMs\", " +
                $"COALESCE(MAX(EXTRACT(EPOCH FROM \"Duration\") * 1000), 0) AS \"MaxMs\", " +
                $"COUNT(*) AS \"CompletedCount\" " +
                $"FROM \"{schema}\".\"Instances\" WHERE \"CompletedAt\" IS NOT NULL AND \"Duration\" IS NOT NULL")
            .FirstOrDefaultAsync(cancellationToken);
        return result is not null
            ? new InstanceDurationStat(result.AvgMs, result.MinMs, result.MaxMs, result.CompletedCount)
            : new InstanceDurationStat(0, 0, 0, 0);
    }

    /// <inheritdoc />
    public async Task<List<StateCountStat>> GetFaultStateCountsAsync(CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        var grouped = await dbSet.AsNoTracking()
            .Where(i => i.Status == InstanceStatus.Faulted)
            .GroupBy(i => i.CurrentState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return grouped.Select(x => new StateCountStat(x.State ?? string.Empty, x.Count)).ToList();
    }
}

/// <summary>SQL projection record for instance duration aggregation (monitor-only, additive).</summary>
internal sealed record InstanceDurationRaw(double AvgMs, double MinMs, double MaxMs, long CompletedCount);