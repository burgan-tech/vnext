using BBT.Aether.Application;
using BBT.Aether.Results;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Runs every registered <see cref="IPublishCompletedHook"/> once, for a deployment that has
/// finished publishing its components.
/// </summary>
public interface IPublishCompletedAppService : IApplicationService
{
    /// <summary>
    /// Executes the hooks and reports what each one did.
    /// </summary>
    /// <param name="input">What the caller reported about the finished deployment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Always <c>Ok</c> when the hooks were run at all — a failing hook is reported inside
    /// <see cref="PublishCompletedOutput.Success"/>, not as a failed call. A <c>Fail</c> here means
    /// the request itself was rejected.
    /// </returns>
    Task<Result<PublishCompletedOutput>> ExecuteAsync(
        PublishCompletedInput input,
        CancellationToken cancellationToken = default);
}
