namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Binding configuration for a State Store task.
/// Carries the resolved command and Dapr state-store options for the invoker.
/// </summary>
public sealed class StateStoreBinding
{
    /// <summary>
    /// Command to execute: <c>get</c>, <c>set</c> or <c>delete</c>.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Optional Dapr state store component name. When null or empty, the executing runtime's
    /// <c>DAPR_STATE_STORE_NAME</c> configuration value is used.
    /// </summary>
    public string? StoreName { get; init; }

    /// <summary>
    /// Cache key for get / set / single-key delete.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// Optional list of keys for bulk delete.
    /// </summary>
    public List<string>? Keys { get; init; }

    /// <summary>
    /// Optional Dapr state Query API filter (raw JSON) for tag/pattern based delete.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    /// Value to write (raw JSON string) for <c>set</c>.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Optional time-to-live in seconds applied on set (Dapr <c>ttlInSeconds</c> metadata).
    /// </summary>
    public int? TtlInSeconds { get; init; }

    /// <summary>
    /// Optional ETag for optimistic concurrency.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>
    /// Optional concurrency mode: <c>FirstWrite</c> or <c>LastWrite</c>.
    /// </summary>
    public string? Concurrency { get; init; }

    /// <summary>
    /// Optional consistency mode: <c>Eventual</c> or <c>Strong</c>.
    /// </summary>
    public string? Consistency { get; init; }

    /// <summary>
    /// Optional additional metadata for the state store operation.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}
