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
    Task<Result<HateoasPagedList<GetInstanceOutput>>> GetInstanceListAsync(
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
    /// Retrieves the complete state information for an instance including data href, view, state, status, correlations, transitions and ETag
    /// </summary>
    Task<Result<GetInstanceStateOutput>> GetInstanceStateAsync(
        GetInstanceStateInput input,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves platform-specific view content for an instance
    /// </summary>
    Task<Result<GetViewOutput>> GetPlatformSpecificViewAsync(
        GetViewInput input,
        string? platform,
        string? transitionKey,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves schema for an instance
    /// </summary>
    Task<Result<GetSchemaOutput>> GetSchemaAsync(
        GetSchemaInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves and executes extensions for an instance
    /// </summary>
    Task<Result<GetExtensionsOutput>> GetExtensionsAsync(
        GetExtensionsInput input,
        CancellationToken cancellationToken = default);
}