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
    Task<Result<List<HumanTaskItemOutput>>> GetHumanTaskInstancesAsync(
        string domain,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken cancellationToken = default);
}