using System.Text.Json;

namespace BBT.Workflow.Tasks;

/// <summary>
/// Raw task envelope for invocation routing.
/// Contains the task type and binding as raw JSON for dynamic deserialization.
/// Used for remote invocation where the binding is serialized.
/// </summary>
public sealed class TaskEnvelope
{
    /// <summary>
    /// Task type discriminator for invoker resolution (e.g., "Http", "DaprService").
    /// </summary>
    public required string TaskType { get; init; }
    
    /// <summary>
    /// Version of the binding schema.
    /// </summary>
    public string Version { get; init; } = "1.0.0";
    
    /// <summary>
    /// Task key for logging and tracing.
    /// </summary>
    public required string TaskKey { get; init; }
    
    /// <summary>
    /// Raw binding configuration as JsonElement for dynamic deserialization.
    /// The actual type depends on TaskType.
    /// </summary>
    public required JsonElement Binding { get; init; }
}

