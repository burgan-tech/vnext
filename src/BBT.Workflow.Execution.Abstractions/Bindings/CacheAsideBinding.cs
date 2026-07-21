namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Binding configuration for a Cache-Aside task.
/// Carries the resolved cache key + state-store options and the pre-resolved source task envelope that
/// the invoker executes on a cache miss. Mirrors the State Store binding for the get/set semantics and
/// adds the read-through specifics (<see cref="SourceTask"/>, <see cref="ForceRefresh"/>,
/// <see cref="BypassOnCacheError"/>).
/// </summary>
public sealed class CacheAsideBinding
{
    /// <summary>
    /// Resolved cache key (already dynamic-resolved in Orchestration). The invoker applies the shared
    /// <c>custom:</c> namespace prefix, exactly like the State Store task.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Optional Dapr state store component name. When null or empty, the executing runtime's
    /// <c>DAPR_STATE_STORE_NAME</c> configuration value is used.
    /// </summary>
    public string? StoreName { get; init; }

    /// <summary>
    /// Optional time-to-live in seconds applied on the cache write (Dapr <c>ttlInSeconds</c> metadata).
    /// Null / non-positive means no expiry.
    /// </summary>
    public int? TtlInSeconds { get; init; }

    /// <summary>
    /// Optional consistency mode: <c>Eventual</c> or <c>Strong</c>.
    /// </summary>
    public string? Consistency { get; init; }

    /// <summary>
    /// When <c>true</c> (default), cache read/write failures fall back to the source task instead of
    /// failing the task; when <c>false</c>, a cache error surfaces as a failure.
    /// </summary>
    public bool BypassOnCacheError { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, the cache read is skipped and the source task is always executed and cached.
    /// </summary>
    public bool ForceRefresh { get; init; }

    /// <summary>
    /// Pre-resolved envelope of the source task executed on a cache miss. The invoker dispatches it
    /// through the local task-invoker registry (so an HTTP source runs on the same Execution service).
    /// </summary>
    public required TaskEnvelope SourceTask { get; init; }
}
