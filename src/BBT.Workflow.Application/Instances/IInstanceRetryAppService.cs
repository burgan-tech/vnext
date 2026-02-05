using BBT.Aether.Application;
using BBT.Aether.Results;

namespace BBT.Workflow.Instances;

/// <summary>
/// Application service for retrying faulted workflow instances.
/// </summary>
public interface IInstanceRetryAppService : IApplicationService
{
    /// <summary>
    /// Retries a faulted workflow instance by re-executing the incomplete transition.
    /// Bypasses tasks that completed successfully before the fault occurred.
    /// </summary>
    /// <param name="input">The retry input parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the retry output or an error.</returns>
    Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default);
}
