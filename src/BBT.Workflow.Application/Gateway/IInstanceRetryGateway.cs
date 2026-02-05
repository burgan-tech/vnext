using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Gateway interface for instance retry operations.
/// Routes between local and remote execution based on target domain.
/// When target domain matches the current runtime, executes locally.
/// When target domain differs, delegates to remote HTTP service.
/// </summary>
public interface IInstanceRetryGateway
{
    /// <summary>
    /// Retries a faulted workflow instance.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The retry input containing domain, workflow, instance, and retry options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the retry output or an error.</returns>
    Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default);
}
