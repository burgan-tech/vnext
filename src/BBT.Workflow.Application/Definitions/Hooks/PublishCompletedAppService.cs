using BBT.Aether.Results;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Definitions;

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>Every hook runs, even after one fails.</b> The hooks are independent pieces of
/// post-deployment work; stopping at the first failure would make the *order* of an unrelated
/// registration decide whether the rest of a deployment's post-work happens at all. Each failure is
/// reported in its own entry instead.
/// </para>
/// <para>
/// <b>Nothing escapes.</b> A hook that throws is recorded as a failure and the loop continues: this
/// endpoint is the last step of a CD pipeline, and a 500 from it would be read as a failed
/// deployment when the components are already published.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// Takes an <see cref="ILogger{TCategoryName}"/> rather than deriving from Aether's
/// <c>ApplicationService</c>: the base's conveniences all resolve through an ambient lazy service
/// provider, and this service needs none of them. Nothing here touches the unit of work, the clock
/// or the mapper.
/// </para>
/// </remarks>
public sealed class PublishCompletedAppService(
    IEnumerable<IPublishCompletedHook> hooks,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<PublishCompletedAppService> logger)
    : IPublishCompletedAppService
{
    /// <inheritdoc />
    public async Task<Result<PublishCompletedOutput>> ExecuteAsync(
        PublishCompletedInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Only when supplied: the field is optional, but a pipeline that names the wrong domain is
        // pointed at the wrong runtime and must hear about it rather than refresh a stranger's cache.
        if (!string.IsNullOrWhiteSpace(input.Domain))
            runtimeInfoProvider.Check(input.Domain);

        // Ordered here rather than relying on registration order, so adding a hook cannot silently
        // reorder the existing ones. Name breaks ties to keep the sequence deterministic.
        var ordered = hooks
            .OrderBy(hook => hook.Order)
            .ThenBy(hook => hook.Name, StringComparer.Ordinal)
            .ToList();

        logger.PublishCompletedReceived(input.PackageName ?? "-", input.Version ?? "-", ordered.Count);

        var results = new List<PublishCompletedHookResult>(ordered.Count);

        foreach (var hook in ordered)
        {
            results.Add(await RunAsync(hook, input, cancellationToken));
        }

        var failed = results.Count(result => result.Outcome == PublishCompletedHookOutcomes.Failed);

        logger.PublishCompletedFinished(ordered.Count, failed);

        return Result<PublishCompletedOutput>.Ok(
            new PublishCompletedOutput(failed == 0, results));
    }

    private async Task<PublishCompletedHookResult> RunAsync(
        IPublishCompletedHook hook,
        PublishCompletedInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await hook.ExecuteAsync(input, cancellationToken);

            if (!result.IsSuccess)
            {
                var reason = result.Error.Message ?? result.Error.Code;
                logger.PublishCompletedHookFailed(hook.Name, reason);

                return new PublishCompletedHookResult(
                    hook.Name, PublishCompletedHookOutcomes.Failed, reason);
            }

            var outcome = string.IsNullOrWhiteSpace(result.Value) ? "Succeeded" : result.Value!;
            logger.PublishCompletedHookSucceeded(hook.Name, outcome);

            return new PublishCompletedHookResult(hook.Name, outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up; do not disguise that as a hook defect.
            throw;
        }
        catch (Exception ex)
        {
            logger.PublishCompletedHookFaulted(ex, hook.Name);

            return new PublishCompletedHookResult(
                hook.Name, PublishCompletedHookOutcomes.Failed, ex.Message);
        }
    }
}
