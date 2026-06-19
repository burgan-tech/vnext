using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BBT.Workflow.HttpApi.Shared.HealthChecks;

/// <summary>
/// Caches an inner health check result for a configurable TTL to avoid hitting the
/// dependency (e.g. PostgreSQL) on every probe. Concurrent calls are gate-kept by
/// a <see cref="SemaphoreSlim"/> so the inner check is invoked at most once per TTL
/// window even under parallel probe load.
/// </summary>
/// <remarks>
/// Dispose this instance when it is no longer needed to release the underlying
/// <see cref="SemaphoreSlim"/>.
/// </remarks>
public sealed class CachedHealthCheck : IHealthCheck, IDisposable
{
    private readonly IHealthCheck _inner;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // _last is only written inside the gate, but read on the lock-free fast path.
    // A Volatile.Read ensures the reading thread sees the most-recently committed value
    // without a full memory barrier. At worst the fast path may briefly see a stale-but-valid
    // result from a just-expired window, causing at most one redundant inner call — acceptable
    // for a health check.
    private HealthCheckResult _last;

    // Stored as UTC ticks (long) so the 64-bit read/write is atomic on 64-bit runtimes.
    // Volatile.Read/Write provides the acquire/release fence needed for the lock-free fast path.
    private long _expiresAtTicks = DateTimeOffset.MinValue.UtcTicks;

    /// <summary>
    /// Initializes a new instance of <see cref="CachedHealthCheck"/>.
    /// </summary>
    /// <param name="inner">The inner health check to wrap and cache.</param>
    /// <param name="ttl">How long to cache the result before re-evaluating.</param>
    /// <param name="timeProvider">Time provider used for cache expiry; defaults to <see cref="TimeProvider.System"/>.</param>
    public CachedHealthCheck(IHealthCheck inner, TimeSpan ttl, TimeProvider? timeProvider = null)
    {
        _inner = inner;
        _ttl = ttl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Fast path: result still fresh. Volatile.Read ensures we see the latest written ticks.
        if (_timeProvider.GetUtcNow().UtcTicks < Volatile.Read(ref _expiresAtTicks))
            return _last;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Double-checked: another caller may have refreshed while we waited
            if (_timeProvider.GetUtcNow().UtcTicks < Volatile.Read(ref _expiresAtTicks))
                return _last;

            _last = await _inner.CheckHealthAsync(context, cancellationToken);
            // Write _last before updating the expiry so the fast path never reads
            // a new expiry paired with a stale result.
            Volatile.Write(ref _expiresAtTicks, (_timeProvider.GetUtcNow() + _ttl).UtcTicks);
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
