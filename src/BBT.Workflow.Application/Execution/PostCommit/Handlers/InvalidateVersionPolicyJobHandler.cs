using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.PostCommit.Handlers;

/// <summary>
/// Post-commit handler that bumps the flow's cache generation after its default-version pointer
/// changed in <c>sys-version-registry</c>, so the next floating resolution reads the new pointer.
/// </summary>
public sealed class InvalidateVersionPolicyJobHandler(
    IDomainCacheContext cacheContext,
    ILogger<InvalidateVersionPolicyJobHandler> logger) : IPostCommitHandler<InvalidateVersionPolicyJob>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        InvalidateVersionPolicyJob job,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Best effort: the registry data is already committed, so a failed bump must neither fail the
        // request nor fault the registry instance. Peers pick the change up when the generation
        // memo/TTL expires.
        try
        {
            // "latest" is not a full version, so this only bumps the generation (no body drop, no warm).
            var result = await cacheContext.Workflows.InvalidateAsync(
                job.Domain, job.FlowKey, InstanceDataVersionComparer.LatestKeyword, cancellationToken);
            if (!result.IsSuccess)
            {
                logger.VersionRegistryInvalidationFailed(
                    job.Domain, job.FlowKey, result.Error.Code, null);
                return Result.Ok();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.VersionRegistryInvalidationFailed(job.Domain, job.FlowKey, ex.GetType().Name, ex);
            return Result.Ok();
        }

        logger.VersionRegistryInvalidated(job.Domain, job.FlowKey);
        return Result.Ok();
    }
}
