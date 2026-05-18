namespace BBT.Workflow.Gateway;

/// <summary>
/// Input for marking an instance Busy and recursively propagating to active SubFlow correlations.
/// </summary>
public record MarkBusyInput
{
    /// <summary>
    /// Target workflow domain.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Target workflow definition key/name.
    /// </summary>
    public required string Workflow { get; init; }

    /// <summary>
    /// Identifier of the instance to mark Busy.
    /// </summary>
    public required Guid InstanceId { get; init; }

    /// <summary>
    /// Workflow version from the SubFlow correlation. Used for schema resolution.
    /// </summary>
    public string? Version { get; init; }
}
