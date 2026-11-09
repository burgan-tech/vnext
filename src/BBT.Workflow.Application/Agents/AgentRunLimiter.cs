using Microsoft.Extensions.Options;

namespace BBT.Workflow.Agents;

/// <summary>
/// Process-wide bulkhead for agent runs (<c>AiAgent:MaxConcurrentRuns</c>). A run that finds no free slot
/// does not wait: it takes the state's fallback, so a provider slowdown cannot pin every transition worker.
/// </summary>
public sealed class AgentRunLimiter(IOptions<AiAgentOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Value.MaxConcurrentRuns));

    /// <summary>Takes a slot without waiting; null when none is free.</summary>
    public IDisposable? TryAcquire() => _slots.Wait(0) ? new Releaser(_slots) : null;

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    private sealed class Releaser(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                slots.Release();
        }
    }
}
