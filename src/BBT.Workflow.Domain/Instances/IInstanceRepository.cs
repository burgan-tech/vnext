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
    /// Finds the single non-terminal instance (status Active or Busy) for the given key, or null
    /// if none exists. Terminal rows (Completed/Faulted/Passive) are ignored, so this is the
    /// authoritative "is this key currently in use?" lookup — unlike <see cref="FindByIdentifierAsync"/>,
    /// which matches any row regardless of status.
    /// </summary>
    Task<Instance?> FindActiveByKeyAsync(string key,
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
    /// Loads a change-tracked instance with only active SubFlow-type correlations.
    /// DataList and SubProcess correlations are NOT loaded.
    /// Designed for lightweight operations that need SubFlow chain traversal
    /// (e.g. recursive busy propagation).
    /// </summary>
    Task<Instance?> FindWithActiveSubFlowAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns active instances with Human state subtype.
    /// Includes DataList for JSON data extraction.
    /// </summary>
    Task<List<Instance>> GetHumanTaskInstancesAsync(CancellationToken cancellationToken = default);


    /// <summary>
    /// Returns the key and version of every active instance without loading <c>InstanceData.Data</c>.
    /// Used by broadcast-receiving pods to discover what to warm from the distributed cache.
    /// </summary>
    Task<List<InstanceKeyModel>> GetActiveInstanceKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total number of instances matching the optional GraphQL filter.
    /// Uses the same filter-application logic as <see cref="GetPagedResultsAsync"/>
    /// but issues a single COUNT query — no rows are transferred.
    /// </summary>
    /// <param name="filter">Optional GraphQL/legacy filter JSON. Null means count all instances.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total row count matching the filter.</returns>
    Task<long> CountAsync(
        string? filter,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Read-only: counts instances matching the given status and optional flowVersion filter.
    /// Uses direct LINQ predicates — does not go through the JSON filter parsing chain.
    /// Pass <c>null</c> for <paramref name="status"/> to count all statuses.
    /// Pass <c>null</c> for <paramref name="flowVersion"/> to count all versions (additive, monitor-only).
    /// </summary>
    Task<long> CountByStatusAsync(
        InstanceStatus? status,
        string? flowVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: counts instances whose <c>CurrentState</c> equals <paramref name="stateKey"/>,
    /// filtered by optional <paramref name="status"/> and optional <paramref name="flowVersion"/>.
    /// Uses direct LINQ predicates (additive, monitor-only).
    /// </summary>
    Task<long> CountByStateAsync(
        string stateKey,
        InstanceStatus? status,
        string? flowVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Read-only: avg/min/max completion duration over completed instances (additive, monitor-only).</summary>
    Task<InstanceDurationStat> GetDurationStatAsync(CancellationToken cancellationToken = default);

    /// <summary>Read-only: per-current-state count of faulted instances in the current schema (additive, monitor-only).</summary>
    Task<List<StateCountStat>> GetFaultStateCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns Busy instances that own an auto-chain token whose heartbeat is older than
    /// <paramref name="olderThanUtc"/> — candidates for the stuck-Busy reaper (S7).
    /// Scoped to the current schema; the reaper sweeps schemas it is invoked for.
    /// </summary>
    /// <param name="olderThanUtc">Heartbeat staleness threshold (UTC).</param>
    /// <param name="maxCount">Maximum number of candidates to return per sweep.</param>
    Task<List<Instance>> GetStuckBusyChainsAsync(
        DateTime olderThanUtc, int maxCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct flow keys registered as definitions in the <c>sys_flows</c> schema.
    /// Each flow key maps to its own runtime-created database schema. Used by background sweeps
    /// (e.g. the stuck-Busy chain reaper) to enumerate the per-flow schemas to scan, since a
    /// hosted service has no request-scoped <c>ICurrentSchema</c> and the real instances live in
    /// the per-flow schemas — not in any single ambient/default schema.
    /// </summary>
    Task<IReadOnlyList<string>> GetActiveFlowKeysAsync(CancellationToken cancellationToken = default);
}
