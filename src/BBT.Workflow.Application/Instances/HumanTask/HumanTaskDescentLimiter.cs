namespace BBT.Workflow.Instances.HumanTask;

/// <summary>
/// Process-wide ceiling on concurrent human-task descent branches.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HumanTaskFunctionOptions.FanoutParallelism"/> bounds ONE request. Nothing bounded the
/// product, and this endpoint has no single-flight to fall back on: the response cache is keyed per
/// caller, so N distinct callers are N independent rebuilds by construction. Each descent branch
/// holds its own unit of work — and therefore its own pooled connection — across EF loads and, on a
/// cross-domain hop, across a network round trip. The result was measured: concurrent distinct
/// callers exhausted the server's connection slots outright (<c>53300 sorry, too many clients
/// already</c>), first at 20 callers and, after the scan was collapsed onto one connection, still at
/// 80.
/// </para>
/// <para>
/// Sized to sit ABOVE what a single request can want, so an uncontended request is untouched and
/// keeps its full <see cref="HumanTaskFunctionOptions.FanoutParallelism"/> width. It binds only when
/// requests coincide — which is exactly the case that had no owner. Queuing there is the intended
/// outcome: a caller waiting a few hundred milliseconds is strictly better than every caller, and
/// the transition pipeline sharing the pool, losing their connections.
/// </para>
/// <para>
/// Deliberately not a connection-string <c>Maximum Pool Size</c>: that would cap the pool for every
/// caller of the database, including the write path, to fix a read endpoint's appetite.
/// </para>
/// </remarks>
public sealed class HumanTaskDescentLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    /// <summary>Initializes the limiter from <see cref="HumanTaskFunctionOptions"/>.</summary>
    public HumanTaskDescentLimiter(Microsoft.Extensions.Options.IOptions<HumanTaskFunctionOptions> options)
    {
        var limit = Math.Max(1, options.Value.MaxConcurrentDescents);
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
                semaphore.Release();
        }
    }
}
