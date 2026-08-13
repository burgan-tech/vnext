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

    /// <inheritdoc />
    public async Task<ITransitionLockScope> AcquireAsync(
        string lockKey,
        CancellationToken cancellationToken = default)
    {
        // Single attempt by design (review decision): a held lock means a concurrent hop is
        // mid-flip; callers surface that as a conflict (409) or proceed unguarded, and the
        // client retry is the back-pressure mechanism — no in-process wait loop.
        var handle = await distributedLockService.TryAcquireLockAsync(
            lockKey,
            _leaseSeconds,
            cancellationToken);

        if (handle is not null)
            return new TransitionLockScope(lockKey, handle, _leaseSeconds, logger);

        logger.StatusLockAcquireFailed(lockKey);
        return TransitionLockScope.NotAcquired(lockKey);
    }
}
