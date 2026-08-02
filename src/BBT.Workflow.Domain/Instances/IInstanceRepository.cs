using BBT.Aether;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Results;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Filtering;

namespace BBT.Workflow.Instances;

public interface IInstanceRepository : IRepository<Instance, Guid>
{
    Task<Instance?> FindByIdentifierAsync(string? identifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the single instance matching an <see cref="InstanceFilter"/> in the current schema —
    /// the entry point of the instance filter engine. Instance columns and JSON attribute paths are
    /// translated to a parameterized query joined to the latest instance-data row, ordered per the
    /// filter, taking the first/last match. Returns null when nothing matches. The returned instance
    /// carries its columns (e.g. <c>Key</c>); the data history is not eagerly loaded.
    /// </summary>
    Task<Instance?> FindByFilterAsync(InstanceFilter filter,
        CancellationToken cancellationToken = default);
    
    Task<Instance?> FindByIdentifierAsReadOnlyAsync(string identifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads several instances by id for read-only projection, including only their data rows.
    /// Unlike <see cref="FindByIdentifierAsReadOnlyAsync"/> this omits the ChildCorrelations include,
    /// because callers of this method never read correlations. Ids with no matching row are omitted.
    /// </summary>
    Task<List<Instance>> FindByIdsAsReadOnlyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds an instance by its identifier (GUID or key) without loading DataList.
    /// Loads ChildCorrelations (active-only) but skips all InstanceData versions.
    /// Non-tracking (AsNoTracking) — intended for monitoring read queries that do not need data history.
    /// </summary>
    Task<Instance?> FindByIdentifierSlimAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Projects the state-function validation fingerprint (effective state, status, flow version,
    /// active-subflow flag) for the instance matching the identifier (GUID or key) in a single
    /// projection query — no includes, no aggregate materialization. Identifier resolution mirrors
    /// <see cref="FindByIdentifierAsReadOnlyAsync"/> (id first, then most recent row by key).
    /// Returns null when no instance matches.
    /// </summary>
    Task<InstanceStateFingerprint?> GetStateFingerprintAsync(string identifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Projects the data-function validation fingerprint (latest data row's ETag + flow version)
    /// for the instance matching the identifier (GUID or key) in a single projection query.
    /// The latest ETag read is an index-only probe (<c>UX_InstancesData_Instance_IsLatest</c>
    /// includes ETag). Identifier resolution mirrors <see cref="FindByIdentifierAsReadOnlyAsync"/>.
    /// Returns null when no instance matches.
    /// </summary>
    Task<InstanceDataFingerprint?> GetDataFingerprintAsync(string identifier,
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
    /// <param name="page">Page number for pagination (1-based).</param>
    /// <param name="pageSize">Page size for pagination.</param>
    /// <param name="filter">Optional filter JSON: a plain GraphQL node or a request envelope embedding groupBy/aggregations.</param>
    /// <param name="groupBy">Optional groupBy JSON ({"fields":[...],"aggregations":{...}}).</param>
    /// <param name="aggregations">Optional standalone aggregations JSON (honored only without groupBy).</param>
    /// <param name="sort">Optional orderBy JSON (e.g. {"field":"createdAt","direction":"desc"} or {"fields":[...]})</param>
    /// <param name="schemaContext">Optional schema-driven filter/sort metadata</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The paged instances, plus group summaries when grouping applies (the paged list is empty in that case).</returns>
    Task<(HateoasPagedList<Instance> PagedList, List<GroupSummary>? Groups)> GetPagedResultsWithGroupsAsync(
        int page,
        int pageSize,
        string? filter,
        string? groupBy = null,
        string? aggregations = null,
        string? sort = null,
        SchemaFilterContext? schemaContext = null,
        CancellationToken cancellationToken = default);

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
    /// Finds an instance including ALL child correlations (completed and active) as a tracked entity.
    /// Required by correlation revert: the default detail load filters out completed correlations,
    /// which would make reverting a just-completed correlation a silent no-op.
    /// </summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The instance with all correlations, or null when not found.</returns>
    Task<Instance?> FindWithAllCorrelationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds an instance including ALL child correlations (completed and active) AND instance data
    /// as a tracked entity. Required by subflow terminal handlers that run output mapping:
    /// <c>Instance.AddData</c> derives the next version and moves the <c>IsLatest</c> flag from the
    /// in-memory data list, so merging onto an aggregate loaded without data would restart
    /// versioning at the default version and leave duplicate latest rows.
    /// </summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The instance with all correlations and data, or null when not found.</returns>
    Task<Instance?> FindWithAllCorrelationsAndDataAsync(
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

    /// <summary>
    /// Read-only: returns per-status instance counts (Active/Busy/Completed/Faulted/Passive) in a single
    /// aggregation query, honouring the optional GraphQL/legacy <paramref name="filter"/> (e.g. a
    /// <c>createdAt</c> date-range). Replaces N separate per-status COUNT round-trips for dashboard
    /// counters. Reuses the same filter-application path as <see cref="CountAsync"/> (additive, monitor-only).
    /// </summary>
    /// <param name="filter">Optional GraphQL/legacy filter JSON. Null counts all instances in the schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<InstanceStatusCounts> GetStatusCountsAsync(
        string? filter,
        CancellationToken cancellationToken = default);

    /// <summary>Read-only: per-current-state count of faulted instances in the current schema (additive, monitor-only).</summary>
    Task<List<StateCountStat>> GetFaultStateCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns Busy instances that own an auto-chain token whose heartbeat is older than
    /// <paramref name="olderThanUtc"/> — candidates for the stuck-Busy reaper (S7).
    /// Scoped to the current schema; the reaper sweeps schemas it is invoked for.
    /// </summary>
    /// <param name="olderThanUtc">Heartbeat staleness threshold (UTC).</param>
    /// <param name="maxCount">Maximum number of candidates to return per sweep.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Returns a paged list of active-instance + data pairs projected to a slim summary,
    /// ordered by instance Id. Excludes unused Instance columns (CurrentState, Status, etc.).
    /// Non-tracking — intended for monitoring component list queries only.
    /// </summary>
    Task<List<ActiveInstanceDataSummary>> GetActiveDataSummariesPagedAsync(
        int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paged, slim projection of all published versions for the component
    /// identified by <paramref name="key"/> in the current schema.
    /// Results are ordered latest-first (<c>IsLatest DESC</c>, <c>PublishedAt DESC</c>).
    /// Pass <paramref name="take"/> as <c>pageSize + 1</c> to determine <c>hasNext</c>
    /// without an extra COUNT query.
    /// Only monitoring consumes this method (additive, monitoring-only).
    /// </summary>
    Task<List<ComponentVersionSummary>> GetVersionsPagedAsync(
        string key,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
