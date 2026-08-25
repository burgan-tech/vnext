using BBT.Aether.DistributedLock;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.Execution.Locks;

/// <summary>
/// Short-lease status lock backed by the Aether distributed lock service. Protects only the
/// instance status check-and-set (reserve / settle), so the lease is a few seconds
/// (<see cref="WorkflowExecutionOptions.StatusLockLeaseSeconds"/>) instead of a chain budget.
/// </summary>
public sealed class InstanceStatusLock(
    IDistributedLockService distributedLockService,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<InstanceStatusLock> logger) : IInstanceStatusLock
{
    private readonly int _leaseSeconds = Math.Max(1, executionOptions.Value.StatusLockLeaseSeconds);

    /// <summary>This funnel's value for <see cref="TelemetryConstants.TagNames.LockKind"/>.</summary>
    private const string LockKind = "status";

    /// <inheritdoc />
    public async Task<ITransitionLockScope> AcquireAsync(
        string lockKey,
        CancellationToken cancellationToken = default)
    {
        // The key is in the span NAME, not only the tag: a contended hop is the thing you look for
        // in a tree, and reading which key it fought over should not require opening the span.
        using var activity = PipelineStepActivityHelper.StartOperationActivity($"Lock.Acquire/{lockKey}");
        activity?.SetTag(TelemetryConstants.TagNames.LockKey, lockKey);
        activity?.SetTag(TelemetryConstants.TagNames.LockLeaseSeconds, _leaseSeconds);
        activity?.SetTag(TelemetryConstants.TagNames.LockKind, LockKind);

        // Single attempt by design (review decision): a held lock means a concurrent hop is
        // mid-flip; callers surface that as a conflict (409) or proceed unguarded, and the
        // client retry is the back-pressure mechanism — no in-process wait loop.
        var handle = await distributedLockService.TryAcquireLockAsync(
            lockKey,
            _leaseSeconds,
            cancellationToken);

        activity?.SetTag(TelemetryConstants.TagNames.LockAcquired, handle is not null);

        if (handle is not null)
            return new TransitionLockScope(lockKey, handle, _leaseSeconds, logger, LockKind);

        logger.StatusLockAcquireFailed(lockKey);
        return TransitionLockScope.NotAcquired(lockKey);
    }
}
