using Microsoft.Extensions.Options;

namespace BBT.Workflow.Tasks.Executors.FanOut;

/// <summary>
/// Process-wide bulkhead for fan-out item execution. Singleton: every fan-out batch in the
/// process draws item slots from this single semaphore, so N concurrent instances cannot
/// multiply into N × maxDegreeOfParallelism downstream calls beyond the configured ceiling.
/// </summary>
/// <remarks>
/// Intentionally not <see cref="System.IDisposable"/>: the underlying <see cref="SemaphoreSlim"/>
/// is registered as a singleton and lives for the process lifetime. Disposing a
/// <see cref="SemaphoreSlim"/> only releases a wait handle that is never allocated unless
/// <c>AvailableWaitHandle</c> is touched, and adding <see cref="System.IDisposable"/> here would
/// create a disposal obligation on the DI container that nothing in this codebase's singleton
/// lifecycle honors (the host process exit reclaims the handle either way).
/// </remarks>
public sealed class FanOutConcurrencyLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _capacity;

    public FanOutConcurrencyLimiter(IOptions<FanOutOptions> options)
    {
        _capacity = options.Value.MaxConcurrentItems;
        _semaphore = new SemaphoreSlim(_capacity, _capacity);
    }

    /// <summary>
    /// Observability gauge only — a racy snapshot of currently held item slots, computed as
    /// capacity minus the semaphore's current count. Never use this to decide whether to wait,
    /// acquire, or release; it exists purely for metrics/logging, not control flow.
    /// </summary>
    public int ActiveCount => _capacity - _semaphore.CurrentCount;

    /// <summary>
    /// Waits for a global fan-out item slot to become available. Callers MUST pair every
    /// successful wait with exactly one <see cref="Release"/>, typically in a
    /// <c>try</c>/<c>finally</c> around the item's execution.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken)
        => _semaphore.WaitAsync(cancellationToken);

    /// <summary>
    /// Releases a previously acquired item slot. An unbalanced call (a <see cref="Release"/>
    /// with no matching <see cref="WaitAsync"/>) is a caller bug — this deliberately does not
    /// guard against it and lets <see cref="SemaphoreFullException"/> propagate, so the defect
    /// surfaces loudly at the call site instead of silently corrupting the bulkhead's capacity.
    /// </summary>
    public void Release() => _semaphore.Release();
}
