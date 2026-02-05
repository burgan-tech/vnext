using BBT.Aether.Results;

namespace BBT.Workflow.Instances.Remote;

/// <summary>
/// Remote service interface for instance retry operations.
/// Acts as a client to the InstanceController retry endpoint for remote workflow instances.
/// </summary>
public interface IRemoteInstanceRetryAppService
{
    /// <summary>
    /// Retries a faulted workflow instance by calling the remote API.
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/retry
    /// </summary>
    /// <param name="input">The retry input containing domain, workflow, instance, and retry options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the retry output or an error.</returns>
    Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default);
}
