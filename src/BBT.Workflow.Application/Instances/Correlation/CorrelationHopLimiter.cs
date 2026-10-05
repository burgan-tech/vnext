using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances.Correlation;

/// <summary>
/// Process-wide ceiling on concurrent correlation-tree hops, spanning all in-flight requests.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InstanceCorrelationOptions.FanoutParallelism"/> bounds ONE request. Their product is
/// what actually meets the connection pool, and this read path has no single-flight to collapse
/// concurrent callers: every caller walks the tree independently. Each hop holds its own unit of
/// work — and therefore its own pooled connection — across EF loads and, on a cross-domain hop,
/// across a network round trip.
/// </para>
/// <para>
/// This is the same ceiling <c>HumanTaskDescentLimiter</c> exists for, for the same reason and
/// after the same measured failure: concurrent distinct callers exhausted the server's connection
/// slots outright (<c>53300 sorry, too many clients already</c>). Copying the bound is cheaper than
/// rediscovering it.
/// </para>
/// <para>
/// Deliberately NOT a connection-string <c>Maximum Pool Size</c>: that would cap the pool for every
/// caller of the database, the write path included, to contain one read endpoint's appetite.
/// </para>
/// </remarks>
public sealed class CorrelationHopLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    /// <summary>Initializes the limiter from <see cref="InstanceCorrelationOptions"/>.</summary>
    public CorrelationHopLimiter(IOptions<InstanceCorrelationOptions> options)
    {
        var limit = Math.Max(1, options.Value.MaxConcurrentHops);
        _semaphore = new SemaphoreSlim(limit, limit);
    }

    /// <summary>Waits for a slot and returns the lease that gives it back.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Lease(_semaphore);
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
