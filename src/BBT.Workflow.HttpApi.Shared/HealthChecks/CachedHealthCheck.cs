using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BBT.Workflow.HttpApi.Shared.HealthChecks;

/// <summary>
/// Caches an inner health check result for a configurable TTL to avoid hitting the
/// dependency (e.g. PostgreSQL) on every probe. Concurrent calls are gate-kept by
/// a <see cref="SemaphoreSlim"/> so the inner check is invoked at most once per TTL
/// window even under parallel probe load.
/// </summary>
public sealed class CachedHealthCheck : IHealthCheck
{
    private readonly IHealthCheck _inner;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthCheckResult _last;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

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
        // Fast path: result still fresh — no lock needed
        if (_timeProvider.GetUtcNow() < _expiresAt)
            return _last;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Double-checked: another caller may have refreshed while we waited
            if (_timeProvider.GetUtcNow() < _expiresAt)
                return _last;

            _last = await _inner.CheckHealthAsync(context, cancellationToken);
            _expiresAt = _timeProvider.GetUtcNow() + _ttl;
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }
}
