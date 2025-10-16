namespace BBT.Workflow.Tasks;

/// <summary>
/// SignalR request model for CloudEvents specification
/// </summary>
public sealed class SignalRRequest
{
    /// <summary>
    /// Instance ID
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Source of the event
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Event type
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Subject of the event
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Event data - GetInstanceStateOutput or GetInstanceStateOutputWithAdditionalData
    /// </summary>
    public object? Data { get; set; }
}

