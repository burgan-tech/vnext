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
    /// Projects the execution-admission snapshot (status, chain token, current state) for the
    /// instance matching the identifier (GUID or key) in a single projection query — no includes,
    /// no aggregate materialization. Used by the Busy pre-check and the reserve re-check under the
    /// status lock. Identifier resolution mirrors <see cref="GetStateFingerprintAsync"/>
    /// (id first, then most recent row by key). Returns null when no instance matches.
    /// </summary>
    Task<InstanceExecutionSnapshot?> GetExecutionSnapshotAsync(string identifier,
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
    /// Include-free variant of <see cref="FindActiveByKeyAsync"/> for existence/status probes
    /// (e.g. the start idempotency check): same non-terminal filter and ordering, but no
    /// DataList or correlation loads.
    /// </summary>
    Task<Instance?> FindActiveByKeyLeanAsync(string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Include-free lookup by primary key with NO key-string fallback — the id counterpart of
    /// <see cref="FindActiveByKeyLeanAsync"/>. Use when the caller holds a typed <see cref="Guid"/>
    /// id (e.g. the start idempotency probe): the generic identifier resolvers would compare
    /// <see cref="Instance.Key"/> against the id string after a miss, which is both meaningless
    /// for a typed id and an extra full-row query.
    /// </summary>
    Task<Instance?> FindLeanByIdAsync(Guid id,
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

    /// <summary>
    /// The no-tracking twin of <see cref="GetResultAsync"/>: same detailed includes and the same
    /// not-found error, but nothing is attached to the caller's unit of work.
    /// </summary>
    /// <remarks>
    /// For request handlers that only READ the aggregate and let an inner unit of work (or the
    /// pipeline) perform the writes. A tracked load there is a live hazard: whatever the handler
    /// mutates in memory is written by the ambient commit at the end of the request, after — and
    /// therefore over — the authoritative value the inner scope persisted.
    /// </remarks>
    Task<Result<Instance>> GetResultAsReadOnlyAsync(
        string identifier,
        CancellationToken cancellationToken = default);

    Task<Result<Instance>> GetResultAsync(string identifier, bool includeDetails = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-set Active → Busy as ONE set-based UPDATE (no aggregate load): the guard is the
    /// WHERE clause, so the returned flag is the authoritative outcome under the caller's status
    /// lock. Busy() raises no domain events, which is what makes the set-based write legal here —
    /// do NOT copy this pattern for event-raising status changes (Complete/Fault/Cancel).
    /// </summary>
    /// <returns>True when exactly this call flipped Active → Busy; false when the instance was
    /// missing, already Busy, or terminal (Completed/Faulted/Passive).</returns>
    Task<bool> TryMarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-set Busy → Active; the set-based counterpart of <see cref="TryMarkBusyAsync(Guid,CancellationToken)"/>
    /// with the same event rules.
    /// </summary>
    /// <returns>True when exactly this call flipped Busy → Active.</returns>
    Task<bool> TryReleaseBusyAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate-aware variant of <see cref="TryMarkBusyAsync(Guid,CancellationToken)"/> for
    /// callers holding the change-tracked instance (pipeline steps, settlement): on a successful CAS
    /// it applies <c>Busy()</c> in memory AND aligns the change tracker's baseline for the status
    /// column, so a later SaveChanges in the same unit of work does not write the status a second
    /// time. On a lost race the aggregate is left untouched.
    /// </summary>
    Task<bool> TryMarkBusyAsync(Instance instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate-aware counterpart of <see cref="TryReleaseBusyAsync(Guid,CancellationToken)"/>:
    /// CAS Busy → Active, then <c>Active()</c> in memory with the same change-tracker baseline
    /// alignment as <see cref="TryMarkBusyAsync(Instance,CancellationToken)"/>.
    /// </summary>
    Task<bool> TryReleaseBusyAsync(Instance instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-set Faulted → Active for a retry: one set-based UPDATE that also clears
    /// <c>CompletedAt</c>, <c>Duration</c> and <c>HasActiveIncident</c>, then applies
    /// <c>Unfault()</c> in memory and aligns the change tracker's baseline for those columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set-based because the retry request loads the aggregate no-tracking in the ambient scope and
    /// the pipeline writes the authoritative status from its own unit of work. Persisting the
    /// unfault through <c>UpdateAsync</c> instead would (a) rewrite the whole aggregate graph —
    /// every <c>InstanceData</c> row included — for a four-column flip, and (b) on a tracked
    /// aggregate leave the ambient unit of work holding a stale Active that its own commit would
    /// later write over the fault the retry produced.
    /// </para>
    /// <para>
    /// Legal as a set-based write because <c>Unfault()</c> raises no domain events (unlike
    /// <c>Complete</c>/<c>Fault</c>/<c>Cancel</c>). <c>ModifiedAt</c> is stamped explicitly since
    /// ExecuteUpdate bypasses the audit interceptor; <c>ModifiedBy</c> is deliberately not
    /// re-stamped, the same rule as the Busy CAS.
    /// </para>
    /// <para>
    /// The caller must resolve the incident rows separately
    /// (<c>IInstanceIncidentRepository.ResolveAllAsync</c>): the flag lives on the instance row, the
    /// incidents do not.
    /// </para>
    /// </remarks>
    /// <returns>True when exactly this call flipped Faulted → Active; false when the instance was
    /// no longer faulted, in which case the aggregate is left untouched.</returns>
    Task<bool> TryUnfaultAsync(Instance instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the long-poll acknowledge token as one set-based UPDATE — the arm is a single
    /// column, so saving the whole aggregate for it was pure overhead. Raises no events.
    /// </summary>
    Task ArmLongPollAckAsync(Guid instanceId, Guid token, CancellationToken cancellationToken = default);

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

    /// <summary>Loads latest parent data and only the correlation being started.</summary>
    Task<Instance?> FindForSubflowStartAsync(
        Guid instanceId,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>Loads latest parent data and only the correlation for the terminal child.</summary>
    Task<Instance?> FindForSubflowCompletionAsync(
        Guid instanceId,
        Guid subInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a change-tracked parent for a SubFlow state change: ONLY the open correlation of the
    /// given sub-instance, and NO data. The state path writes
    /// <see cref="Instance.EffectiveState"/>/type/subtype and reads only <c>ExtraProperties</c> for
    /// the upward event, so pulling <see cref="Instance.DataList"/> — which the default detail load
    /// does, unsplit — costs the whole jsonb history for nothing on the runtime's highest-volume
    /// subflow signal. The aggregate is marked partially loaded; <see cref="Instance.LatestData"/>
    /// is null and must not be read.
    /// </summary>
    Task<Instance?> FindForSubflowStateChangeAsync(
        Guid instanceId,
        Guid subInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the parent for post-commit settlement: always its open correlations (the settlement
    /// guard and the fault cascade read them), and its latest data row only when
    /// <paramref name="includeLatestData"/> is set. The returned aggregate is marked partially
    /// loaded either way; with the data excluded, <see cref="Instance.LatestData"/> is null and
    /// must not be read. The decision of when data is needed lives with the caller — see the
    /// decision matrix on <c>PostCommitParentMutationService</c>.
    /// </summary>
    Task<Instance?> FindForPostCommitSettlementAsync(
        Guid instanceId,
        bool includeLatestData,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes the instance's <b>unresolved</b> incidents onto the aggregate. Incidents live in
    /// their own table and are never included by any load path; this is the single way to bring them
    /// in. Cheap by design: when <see cref="Instance.HasActiveIncident"/> is false no query is issued
    /// and the aggregate is simply marked loaded, so callers may invoke it unconditionally.
    /// Works for tracked aggregates (collection load through the change tracker, so a subsequent
    /// <see cref="Instance.ResolveOpenIncidents"/> is persisted by the next update) and for
    /// no-tracking aggregates (rows are read no-tracking and accepted onto the aggregate).
    /// </summary>
    /// <param name="instance">The aggregate to load incidents for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LoadActiveIncidentsAsync(Instance instance, CancellationToken cancellationToken = default);

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
