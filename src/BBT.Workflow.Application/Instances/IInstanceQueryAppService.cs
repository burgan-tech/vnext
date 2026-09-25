using BBT.Aether;
using BBT.Aether.Application;
using BBT.Aether.Domain.Repositories;
using BBT.Aether.Results;
using BBT.Workflow.Instances.DTOs;

namespace BBT.Workflow.Instances;

public interface IInstanceQueryAppService : IApplicationService
{
    /// <summary>
    /// Retrieves a single instance with optional extensions for data enrichment
    /// </summary>
    Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a paginated list of instances with optional extensions
    /// </summary>
    Task<Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> GetInstanceListAsync(
        GetInstanceListInput input,
        CancellationToken cancellationToken = default);
 

    /// <summary>
    /// Retrieves the complete history of an instance (all data transitions)
    /// </summary>
    Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the task execution history of an instance in execution order — the tasks system
    /// function. Execution metadata only — the journaled payloads are not exposed on any API.
    /// Gated by the same <c>queryRoles</c> check as the state function.
    /// </summary>
    Task<Result<GetInstanceTasksOutput>> GetInstanceTasksAsync(
        GetInstanceTasksInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the recorded actions (execution sub-steps) of one task journal entry in execution
    /// order — the actions system function. <c>NotFound</c> when the task does not belong to
    /// the instance. Gated by the same <c>queryRoles</c> check as the state function.
    /// </summary>
    Task<Result<GetInstanceTaskActionsOutput>> GetInstanceTaskActionsAsync(
        GetInstanceTaskActionsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the attempts model of one transition key on an instance — every firing of that
    /// transition, each with the tasks that ran under it (vnext-client-sdk-core#60). Read-only over
    /// the already-journaled transition/task rows.
    /// </summary>
    Task<Result<GetInstanceMetricsOutput>> GetTransitionMetricsAsync(
        GetTransitionMetricsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the attempts model of one state on an instance — every visit (entry→exit), each with
    /// the state's onEntry and onExit tasks (vnext-client-sdk-core#60). Read-only over the
    /// already-journaled transition/task rows.
    /// </summary>
    Task<Result<GetInstanceMetricsOutput>> GetStateMetricsAsync(
        GetStateMetricsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pages the error-boundary incident history of an instance, newest first. Gated by the same
    /// <c>queryRoles</c> check as the state function.
    /// </summary>
    Task<Result<GetInstanceIncidentsOutput>> GetInstanceIncidentsAsync(
        GetInstanceIncidentsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the newest unresolved incident of an instance, or <c>NotFound</c> when none is open.
    /// The target of the <c>incident.active</c> link; gated by the same <c>queryRoles</c> check as the
    /// state function.
    /// </summary>
    Task<Result<IncidentDetailDto>> GetActiveInstanceIncidentAsync(
        GetActiveInstanceIncidentInput input,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves only the instance data (attributes) with optional ETag support
    /// </summary>
    Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves the complete state information for an instance including data href, view, state, status, correlations, transitions and ETag.
    /// Returns ConditionalResult for If-None-Match support (304 when representation unchanged).
    /// </summary>
    Task<ConditionalResult<GetInstanceStateOutput>> GetInstanceStateAsync(
        GetInstanceStateInput input,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves platform-specific view content for an instance
    /// </summary>
    Task<Result<GetViewOutput>> GetViewAsync(
        GetViewInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves schema for an instance. Supports conditional reads (If-None-Match → 304)
    /// via the fingerprint ETag.
    /// </summary>
    Task<ConditionalResult<GetSchemaOutput>> GetSchemaAsync(
        GetSchemaInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves and executes extensions for an instance
    /// </summary>
    Task<Result<GetExtensionsOutput>> GetExtensionsAsync(
        GetExtensionsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the flow-level master schema an instance is bound to.
    /// If the instance has an active SubFlow, the request is forwarded to the SubFlow instance.
    /// Supports conditional reads (If-None-Match → 304) via the fingerprint ETag.
    /// </summary>
    Task<ConditionalResult<GetSchemaOutput>> GetMasterAsync(
        GetMasterInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the runtime hierarchy of an instance as a recursive tree.
    /// Includes direct and indirect child subflow/subprocess instances.
    /// </summary>
    Task<Result<GetInstanceHierarchyOutput>> GetInstanceHierarchyAsync(
        GetInstanceHierarchyInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves active instances with Human state subtype across all workflow schemas,
    /// filtered by transition role authorization.
    /// </summary>
    /// <param name="domain">The runtime domain to enumerate.</param>
    /// <param name="headers">
    /// Request headers, used to resolve the caller's roles — <c>ICurrentUser.Roles</c> when present,
    /// otherwise the legacy <c>role</c> header — and to supply the <c>$.context.Headers.*</c> namespace
    /// for dynamic role grants.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The rows plus a truncation flag. The flag never reaches the JSON body — that stays a bare
    /// array for the consumer — so the HTTP layer surfaces it as a response header instead.
    /// </returns>
    /// <param name="cacheOverride">
    /// True to skip the cache READ for this request. The result is still written back behind the
    /// same single-flight gate — skipping the write too would let an unauthenticated caller make
    /// this endpoint more expensive than it is with no cache at all.
    /// </param>
    Task<Result<HumanTask.HumanTaskListOutput>> GetHumanTaskInstancesAsync(
        string domain,
        IReadOnlyDictionary<string, string?>? headers = null,
        bool cacheOverride = false,
        CancellationToken cancellationToken = default);
}