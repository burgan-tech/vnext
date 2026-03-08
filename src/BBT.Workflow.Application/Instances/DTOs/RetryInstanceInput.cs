namespace BBT.Workflow.Instances;

/// <summary>
/// Input parameters for retrying a faulted workflow instance.
/// </summary>
public sealed class RetryInstanceInput
{
    /// <summary>
    /// The domain/tenant identifier.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The workflow key.
    /// </summary>
    public required string Workflow { get; init; }

    /// <summary>
    /// The instance identifier (ID or key).
    /// </summary>
    public required string Instance { get; init; }

    /// <summary>
    /// Whether to execute synchronously.
    /// </summary>
    public bool Sync { get; init; }

    /// <summary>
    /// Optional data payload for the retry execution.
    /// </summary>
    public TransitionDataInput? Data { get; init; }

    /// <summary>
    /// Request headers for context propagation.
    /// </summary>
    public Dictionary<string, string?> Headers { get; init; } = new();

    /// <summary>
    /// Route values from the HTTP request.
    /// </summary>
    public Dictionary<string, string?> RouteValues { get; init; } = new();
}
