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
/// (<see cref="WorkflowExecutionOptions.StatusLockLeaseSeconds"/>) instead of the chain budget.
/// <para>
/// Honors <see cref="ChainLockRegistry"/> so that, while the legacy whole-chain lock mode still
/// holds the same key for the duration of the pipeline, status writes from within that pipeline
/// acquire a reentrant no-op scope instead of colliding with their own chain lock.
/// </para>
/// </summary>
public sealed class InstanceStatusLock(
    IDistributedLockService distributedLockService,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<InstanceStatusLock> logger) : IInstanceStatusLock
{
    private readonly int _leaseSeconds = Math.Max(1, executionOptions.Value.StatusLockLeaseSeconds);
    private readonly LockAcquireWait _wait = executionOptions.Value.StatusLockRetry.ToLockAcquireWait();

    /// <inheritdoc />
    public async Task<ITransitionLockScope> AcquireAsync(
        string lockKey,
        CancellationToken cancellationToken = default)
    {
        if (ChainLockRegistry.IsHeld(lockKey))
        {
            logger.TransitionLockReentrantAcquired(lockKey);
            return TransitionLockScope.Reentrant(lockKey);
        }

        for (var attempt = 1; attempt <= _wait.MaxAttempts; attempt++)
        {
            var handle = await distributedLockService.TryAcquireLockAsync(
                lockKey,
                _leaseSeconds,
                cancellationToken);

            if (handle is not null)
                return new TransitionLockScope(lockKey, handle, _leaseSeconds, logger);

            if (attempt == _wait.MaxAttempts)
                break;

            // Jittered backoff: status flips resolve in milliseconds, so contenders must not
            // retry in lockstep or they keep colliding on every attempt.
            var baseDelay = _wait.Delay.TotalMilliseconds * attempt;
            var jitter = Random.Shared.NextDouble() * _wait.Delay.TotalMilliseconds;
            var delay = TimeSpan.FromMilliseconds(baseDelay + jitter);

            logger.TransitionLockRetryScheduled(lockKey, attempt, _wait.MaxAttempts, (int)delay.TotalMilliseconds);

            await Task.Delay(delay, cancellationToken);
        }

        logger.StatusLockAcquireFailed(lockKey);
        return TransitionLockScope.NotAcquired(lockKey);
    }
}
