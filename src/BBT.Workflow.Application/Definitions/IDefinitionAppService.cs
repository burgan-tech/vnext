using BBT.Aether.Application;
using BBT.Aether.Results;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Administrative service interface for workflow management operations.
/// All methods return Result types following Railway pattern - no exceptions for domain errors.
/// </summary>
public interface IDefinitionAppService : IApplicationService
{
    /// <summary>
    /// Publishes a workflow definition to the system.
    /// </summary>
    /// <param name="input">The publish input containing workflow definition, key, flow, version and optional data</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Result indicating success or failure with appropriate error</returns>
    /// <remarks>
    /// Validates the workflow schema, creates or updates workflow instances,
    /// handles versioning, and processes additional data if provided.
    /// Returns Result.Fail when schema is invalid, validation fails, or version already exists.
    /// </remarks>
    Task<Result> PublishAsync(PublishInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates cache for a specific workflow instance.
    /// </summary>
    /// <param name="input">The invalidate cache input containing flow, key, version and domain information</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Result indicating success or failure with appropriate error</returns>
    /// <remarks>
    /// Finds the specified workflow instance and processes it through the cast processor.
    /// Returns Result.Fail when schema is invalid or entity is not found.
    /// </remarks>
    Task<Result> InvalidateCacheAsync(InvalidateCacheInput input, CancellationToken cancellationToken = default);
}