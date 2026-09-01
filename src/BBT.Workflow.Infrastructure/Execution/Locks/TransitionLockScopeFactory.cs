using BBT.Aether.DistributedLock;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.Execution.Locks;

/// <summary>
/// Creates request-scoped lock scopes backed by the Aether distributed lock service.
/// Each scope wraps an <see cref="IDistributedLockHandle"/> and delegates
/// extend / release operations to the underlying handle.
/// <para>
/// The lease is sized to cover the entire auto-chain budget
/// (<see cref="WorkflowExecutionOptions.GetEffectiveLockLeaseSeconds"/>, default
/// <c>TransitionJobTimeoutSeconds + 30</c>) rather than relying on per-hop extension:
/// the Dapr lock provider cannot extend a held lock (its Redis component uses
/// <c>SET NX</c>, which rejects same-owner re-acquire), so a short lease with
/// between-hop extension would silently expire mid-chain.
/// </para>
/// </summary>
public sealed class TransitionLockScopeFactory(
    IDistributedLockService distributedLockService,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<TransitionLockScopeFactory> logger) : ITransitionLockScopeFactory
{
    private readonly int _leaseSeconds = executionOptions.Value.GetEffectiveLockLeaseSeconds();

    /// <summary>This funnel's value for <see cref="TelemetryConstants.TagNames.LockKind"/>.</summary>
    private const string LockKind = "chain";

    /// <inheritdoc />
    public Task<ITransitionLockScope> AcquireAsync(
        string lockKey,
        CancellationToken cancellationToken = default)
        => AcquireAsync(lockKey, LockAcquireWait.None, cancellationToken);

    /// <inheritdoc />
    public async Task<ITransitionLockScope> AcquireAsync(
        string lockKey,
        LockAcquireWait wait,
        CancellationToken cancellationToken = default)
    {
        // Key in the span name — see InstanceStatusLock.AcquireAsync for the rationale.
        using var activity = PipelineStepActivityHelper.StartOperationActivity($"Lock.Acquire/{lockKey}");
        activity?.SetTag(TelemetryConstants.TagNames.LockKey, lockKey);
        activity?.SetTag(TelemetryConstants.TagNames.LockLeaseSeconds, _leaseSeconds);
        activity?.SetTag(TelemetryConstants.TagNames.LockKind, LockKind);

        var attempts = Math.Max(1, wait.MaxAttempts);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var handle = await distributedLockService.TryAcquireLockAsync(
                lockKey,
                _leaseSeconds,
                cancellationToken);

            if (handle is not null)
            {
                logger.LogDebug("Transition lock acquired for {LockKey} (lease={LeaseSeconds}s, attempt={Attempt})",
                    lockKey, _leaseSeconds, attempt);

                activity?.SetTag(TelemetryConstants.TagNames.LockAcquired, true);
                return new TransitionLockScope(lockKey, handle, _leaseSeconds, logger, LockKind);
            }

            if (attempt == attempts)
            {
                break;
            }

            // Jittered backoff: concurrent duplicate deliveries of the same key must not retry in
            // lockstep, or they would keep colliding on every attempt.
            var baseDelay = wait.Delay.TotalMilliseconds * attempt;
            var jitter = Random.Shared.NextDouble() * wait.Delay.TotalMilliseconds;
            var delay = TimeSpan.FromMilliseconds(baseDelay + jitter);

            logger.TransitionLockRetryScheduled(lockKey, attempt, attempts, (int)delay.TotalMilliseconds);

            await Task.Delay(delay, cancellationToken);
        }

        logger.InstanceLockFailed(lockKey);
        activity?.SetTag(TelemetryConstants.TagNames.LockAcquired, false);
        return TransitionLockScope.NotAcquired(lockKey);
    }
}

/// <summary>
/// Wraps an <see cref="IDistributedLockHandle"/> with domain-specific semantics.
/// Implements <see cref="ITransitionLockScope"/> for pipeline consumption.
/// </summary>
internal sealed class TransitionLockScope : ITransitionLockScope
{
    private readonly IDistributedLockHandle? _handle;
    private readonly int _leaseSeconds;
    private readonly ILogger _logger;

    /// <summary>
    /// This scope's <see cref="TelemetryConstants.TagNames.LockKind"/> value ("status" | "chain"),
    /// stamped by whichever funnel constructed it. Null for the non-acquiring construction paths
    /// (<see cref="NotAcquired"/>/<see cref="Reentrant"/>), which never emit a Release span.
    /// </summary>
    private readonly string? _kind;

    internal TransitionLockScope(
        string lockKey,
        IDistributedLockHandle handle,
        int leaseSeconds,
        ILogger logger,
        string kind)
    {
        LockKey = lockKey;
        IsAcquired = true;
        _handle = handle;
        _leaseSeconds = leaseSeconds;
        _logger = logger;
        _kind = kind;
    }

    private TransitionLockScope(string lockKey, bool isAcquired)
    {
        LockKey = lockKey;
        IsAcquired = isAcquired;
        _handle = null;
        _leaseSeconds = 0;
        _logger = null!;
        _kind = null;
    }

    /// <inheritdoc />
    public bool IsAcquired { get; }

    /// <inheritdoc />
    public string LockKey { get; }

    /// <inheritdoc />
    public async Task<bool> ExtendAsync(CancellationToken cancellationToken = default)
    {
        // A reentrant scope has no handle of its own; the outer holder owns the lease
        // (sized upfront to cover the chain budget), so report extension as succeeded.
        if (_handle is null) return IsAcquired;

        var extended = await _handle.ExtendAsync(_leaseSeconds, cancellationToken);

        if (!extended)
        {
            _logger.LogDebug("Failed to extend transition lock for {LockKey}", LockKey);
        }

        return extended;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_handle is not null)
        {
            using var activity = PipelineStepActivityHelper.StartOperationActivity($"Lock.Release/{LockKey}");
            activity?.SetTag(TelemetryConstants.TagNames.LockKey, LockKey);
            activity?.SetTag(TelemetryConstants.TagNames.LockKind, _kind);
            await _handle.DisposeAsync();
            _logger.LogDebug("Transition lock released for {LockKey}", LockKey);
        }
    }

    internal static TransitionLockScope NotAcquired(string lockKey) => new(lockKey, isAcquired: false);

    /// <summary>
    /// Creates an acquired scope without an underlying handle for a key the current execution
    /// chain already holds. Disposal is a no-op so the outer holder's lock is never released.
    /// </summary>
    internal static TransitionLockScope Reentrant(string lockKey) => new(lockKey, isAcquired: true);
}
