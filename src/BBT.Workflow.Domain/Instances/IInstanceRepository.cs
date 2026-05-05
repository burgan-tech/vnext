using BBT.Aether;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Results;
using BBT.Workflow.Definitions.Schemas;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Instances;

public interface IInstanceRepository : IRepository<Instance, Guid>
{
    Task<Instance?> FindByIdentifierAsync(string? identifier,
        CancellationToken cancellationToken = default);
    
    Task<Instance?> FindByIdentifierAsReadOnlyAsync(string identifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a read-only (no-tracking) instance with the full <see cref="Instance.DataList"/>
    /// history. Dedicated to <c>GetInstanceHistoryAsync</c> where detached entities are sufficient.
    /// </summary>
    Task<Instance?> FindByIdentifierWithFullHistoryAsync(string identifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a change-tracked instance with the full <see cref="Instance.DataList"/>.
    /// Use for write paths that need to inspect non-latest versions
    /// (e.g. duplicate version checks during publish).
    /// </summary>
    Task<Instance?> FindByIdentifierWithFullDataAsync(string? identifier,
        CancellationToken cancellationToken = default);

    Task<Result<Instance>> GetActiveAsync(string identifier, CancellationToken cancellationToken = default);
    
    Task<List<InstanceAndDataModel>> GetActiveDataListAsync(CancellationToken cancellationToken = default);

    Task<List<InstanceAndDataModel>> GetActiveDataListPagedAsync(int skip, int take, CancellationToken cancellationToken = default);

    Task<List<InstanceAndDataModel>> GetActiveDataListSinceAsync(DateTime since, int skip, int take, CancellationToken cancellationToken = default);

    Task<InstanceAndDataModel?> FindActiveDataAsync(string key, string version,
        CancellationToken cancellationToken = default);

    Task<List<InstanceAndDataModel>> GetActiveDataListByKeyAsync(string key,
        CancellationToken cancellationToken = default);
        
    Task<HateoasPagedList<Instance>> GetPagedResultsAsync(
        int page,
        int pageSize,
        string? filter,
        string? groupBy = null,
        string? aggregations = null,
        CancellationToken cancellationToken = default,
        SchemaFilterContext? schemaContext = null);

    /// <summary>
    /// Gets paged results with optional groups for groupBy queries
    /// </summary>
    /// <param name="sort">Optional orderBy JSON (e.g. {"field":"createdAt","direction":"desc"} or {"fields":[...]})</param>
    /// <param name="schemaContext">Optional schema-driven filter/sort metadata</param>
    Task<(HateoasPagedList<Instance> PagedList, List<GroupSummary>? Groups)> GetPagedResultsWithGroupsAsync(
        int page,
        int pageSize,
        string? filter,
        string? groupBy = null,
        string? aggregations = null,
        string? sort = null,
        CancellationToken cancellationToken = default,
        SchemaFilterContext? schemaContext = null);

    /// <summary>
    /// Gets paged results with optional groups using parsed GraphQL filter request (optimized - avoids parse-serialize cycle)
    /// </summary>
    Task<(HateoasPagedList<Instance> PagedList, List<GroupSummary>? Groups)> GetPagedResultsWithGroupsAsync(
        int page,
        int pageSize,
        Definitions.GraphQL.GraphQLFilterRequest? request,
        CancellationToken cancellationToken = default);

    Task<Result<Instance>> GetResultAsync(string identifier, bool includeDetails = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an active instance exists with the specified key, excluding the given instance ID.
    /// </summary>
    /// <param name="key">The key to check for duplicates.</param>
    /// <param name="excludeInstanceId">The instance ID to exclude from the check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if an active instance with the same key exists, false otherwise.</returns>
    Task<bool> AnyActiveByKeyAsync(string key, Guid excludeInstanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the key and version of every active instance without loading <c>InstanceData.Data</c>.
    /// Used by broadcast-receiving pods to discover what to warm from the distributed cache.
    /// </summary>
    Task<List<InstanceKeyModel>> GetActiveInstanceKeysAsync(CancellationToken cancellationToken = default);
}