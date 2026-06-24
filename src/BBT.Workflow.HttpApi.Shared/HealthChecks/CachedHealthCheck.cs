using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BBT.Workflow.HttpApi.Shared.HealthChecks;

/// <summary>
/// Throttles a wrapped <see cref="IHealthCheck"/> by caching its result for a configurable TTL.
/// Must be registered as a singleton so the TTL state persists across probes.
/// A SemaphoreSlim prevents thundering-herd: only one live DB query runs at a time.
/// </summary>
public sealed class CachedHealthCheck : IHealthCheck, IDisposable
{
    private readonly IHealthCheck _inner;
    private readonly long _ttlTicks;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthCheckResult? _cached;
    private long _expiresAt = long.MinValue;

    public CachedHealthCheck(IHealthCheck inner, TimeSpan ttl, TimeProvider timeProvider)
    {
        _inner = inner;
        _timeProvider = timeProvider;
        _ttlTicks = (long)(ttl.TotalSeconds * timeProvider.TimestampFrequency);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Fast path — no lock needed if result is still fresh
        if (_cached.HasValue && _timeProvider.GetTimestamp() < _expiresAt)
            return _cached.Value;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside lock (another thread may have refreshed while we waited)
            if (_cached.HasValue && _timeProvider.GetTimestamp() < _expiresAt)
                return _cached.Value;

            _cached = await _inner.CheckHealthAsync(context, cancellationToken);
            _expiresAt = _timeProvider.GetTimestamp() + _ttlTicks;
            return _cached.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
