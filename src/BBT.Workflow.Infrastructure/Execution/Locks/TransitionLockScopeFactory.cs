using BBT.Aether.DistributedLock;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Infrastructure.Execution.Locks;

/// <summary>
/// Creates request-scoped lock scopes backed by the Aether distributed lock service.
/// Each scope wraps an <see cref="IDistributedLockHandle"/> and delegates
/// extend / release operations to the underlying handle.
/// </summary>
public sealed class TransitionLockScopeFactory(
    IDistributedLockService distributedLockService,
    ILogger<TransitionLockScopeFactory> logger) : ITransitionLockScopeFactory
{
    /// <summary>
    /// Default lock lease duration in seconds.
    /// Doubled compared to the old per-transition lease (60s) because the lock
    /// now covers the entire auto-chain. ExtendAsync resets the TTL between iterations.
    /// </summary>
    private const int DefaultLeaseSeconds = 120;

    /// <inheritdoc />
    public async Task<ITransitionLockScope> AcquireAsync(
        string lockKey,
        CancellationToken cancellationToken = default)
    {
        var handle = await distributedLockService.TryAcquireLockAsync(
            lockKey,
            DefaultLeaseSeconds,
            cancellationToken);

        if (handle is null)
        {
            logger.InstanceLockFailed(lockKey);
            return TransitionLockScope.NotAcquired(lockKey);
        }

        logger.LogDebug("Transition lock acquired for {LockKey} (lease={LeaseSeconds}s)",
            lockKey, DefaultLeaseSeconds);

        return new TransitionLockScope(lockKey, handle, DefaultLeaseSeconds, logger);
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

    internal TransitionLockScope(
        string lockKey,
        IDistributedLockHandle handle,
        int leaseSeconds,
        ILogger logger)
    {
        LockKey = lockKey;
        IsAcquired = true;
        _handle = handle;
        _leaseSeconds = leaseSeconds;
        _logger = logger;
    }

    private TransitionLockScope(string lockKey)
    {
        LockKey = lockKey;
        IsAcquired = false;
        _handle = null;
        _leaseSeconds = 0;
        _logger = null!;
    }

    /// <inheritdoc />
    public bool IsAcquired { get; }

    /// <inheritdoc />
    public string LockKey { get; }

    /// <inheritdoc />
    public async Task<bool> ExtendAsync(CancellationToken cancellationToken = default)
    {
        if (_handle is null) return false;

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
            await _handle.DisposeAsync();
            _logger.LogDebug("Transition lock released for {LockKey}", LockKey);
        }
    }

    internal static TransitionLockScope NotAcquired(string lockKey) => new(lockKey);
}
