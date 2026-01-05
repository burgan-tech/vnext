using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Gateway interface for instance query operations.
/// Routes between local and remote execution based on target domain.
/// When target domain matches the current runtime, executes locally.
/// When target domain differs, delegates to remote HTTP service.
/// </summary>
public interface IInstanceQueryGateway
{
    /// <summary>
    /// Retrieves a single instance with optional extensions for data enrichment.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The get instance input containing domain, workflow, and instance identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Conditional result containing the instance output or not modified status.</returns>
    Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves only the instance data (attributes) with optional ETag support and extensions.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The get instance data input containing domain, workflow, instance, and extensions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Conditional result containing the instance data output or not modified status.</returns>
    Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the complete history of an instance (all data transitions).
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The get instance history input containing domain, workflow, and instance identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the instance history output.</returns>
    Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the complete state information for an instance.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The get function with instance input containing domain, workflow, and instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the instance state output.</returns>
    Task<Result<GetInstanceStateOutput>> GetFunctionWithStateAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves platform-specific view content for an instance.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The get function with instance input containing domain, workflow, and instance.</param>
    /// <param name="platform">Optional platform identifier (web, ios, android).</param>
    /// <param name="transitionKey">Optional transition key for transition-specific views.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the view output.</returns>
    Task<Result<GetViewOutput>> GetFunctionWithViewAsync(
        GetFunctionWithInstanceInput input,
        string? platform,
        string? transitionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves schema for an instance transition.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The get function with instance input containing domain, workflow, and instance.</param>
    /// <param name="transitionKey">The transition key to get schema for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the schema output.</returns>
    Task<Result<GetSchemaOutput>> GetFunctionWithSchemaAsync(
        GetFunctionWithInstanceInput input,
        string transitionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves and executes extensions for an instance.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The get function with instance input containing domain, workflow, instance, and extensions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the extensions output.</returns>
    Task<Result<GetExtensionsOutput>> GetFunctionWithExtensionsAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default);
}

