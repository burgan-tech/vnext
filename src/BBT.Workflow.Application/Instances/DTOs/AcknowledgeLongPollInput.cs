namespace BBT.Workflow.Instances;

/// <summary>
/// Input parameters for acknowledging a long-poll termination signal and resuming the paused pipeline.
/// </summary>
public sealed class AcknowledgeLongPollInput
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
    /// Optional workflow version for schema/definition resolution.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// The caller role used for the long-poll acknowledge authorization check.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Request headers for context propagation.
    /// </summary>
    public Dictionary<string, string?> Headers { get; init; } = new();
}
