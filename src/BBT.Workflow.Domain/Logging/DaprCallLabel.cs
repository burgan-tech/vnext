namespace BBT.Workflow.Logging;

/// <summary>
/// Ambient label for the Dapr sidecar call about to be made on this async flow: the cache or lock
/// key the call targets. <c>DaprSpanLabelProcessor</c> reads it in <c>OnStart</c> and stamps it
/// onto the gRPC client span (<c>dapr.proto.runtime.v1.Dapr/GetState</c> etc.) as
/// <c>vnext.dapr.key</c> — the key travels in the protobuf body, so the instrumentation cannot
/// see it and every state/lock span would otherwise be indistinguishable.
/// <para>
/// Set via <see cref="Use"/> immediately around the Dapr-backed call (the labelling decorators on
/// <c>IDistributedCacheService</c>/<c>IDistributedLockService</c> and the resource-lock service do
/// this). <see cref="System.Threading.AsyncLocal{T}"/> flows with the ExecutionContext, so
/// parallel calls are isolated and the label reaches the activity the call starts internally.
/// Disposal restores the previous value, so nesting (a cache read inside an ExecuteWithLock body)
/// unwinds correctly.
/// </para>
/// </summary>
public static class DaprCallLabel
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The key the current async flow's next Dapr data-plane call targets, if any.</summary>
    public static string? Current => Ambient.Value;

    /// <summary>
    /// Sets the ambient label for the duration of the returned scope; dispose restores the
    /// previous value.
    /// </summary>
    public static IDisposable Use(string key)
    {
        var previous = Ambient.Value;
        Ambient.Value = key;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
