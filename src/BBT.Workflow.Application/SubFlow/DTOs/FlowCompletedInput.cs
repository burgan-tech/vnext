using System.Text.Json;
using BBT.Aether;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Data payload for flow completion event.
/// Contains all necessary information about the completed flow instance and its data.
/// </summary>
public record FlowCompletedInput
{
    /// <summary>
    /// The ID of the Parent instance
    /// </summary>
    public required Guid InstanceId { get; init; }

    /// <summary>
    /// The ID of the root instance in the SubItem chain, when available
    /// </summary>
    public Guid? RootInstanceId { get; init; }
    
    /// <summary>
    /// The domain of the parent
    /// </summary>
    public required string Domain { get; init; }
    
    /// <summary>
    /// The workflow name of the parent
    /// </summary>
    public required string Flow { get; init; }
    
    /// <summary>
    /// The version of the parent
    /// </summary>
    public required string? Version { get; init; }

    /// <summary>
    /// The ID of the completed SubItem instance
    /// </summary>
    public required Guid SubInstanceId { get; init; }
    
    /// <summary>
    /// The final state where the SubItem completed
    /// </summary>
    public required string CompletedState { get; init; }
    
    /// <summary>
    /// The complete instance data of the completed SubItem
    /// </summary>
    public JsonElement? InstanceData { get; init; }
    
    /// <summary>
    /// When the SubItem was completed
    /// </summary>
    public required DateTime CompletedAt { get; init; }
    
    /// <summary>
    /// Duration of the SubItem execution
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Whether the completing pipeline chain was executed with a synchronous caller
    /// (sync=true). The parent resume keeps the chain synchronous when set.
    /// </summary>
    public bool Sync { get; init; }

    /// <summary>
    /// Trace lane anchor (W3C traceparent) of the completing subflow. See <c>WorkflowTraceLane</c>.
    /// <para>
    /// Carried in this internal-only body rather than a header, exactly like <c>CorrelationId</c>:
    /// public endpoints must not be able to inject a lane, or a caller could graft its spans onto
    /// an unrelated trace. <c>FlatLaneActivity</c>'s trace-id check is the backstop.
    /// </para>
    /// </summary>
    public string? TraceRoot { get; init; }

    /// <summary>The enclosing lane's anchor, so the subflow's resume returns to the parent's lane.</summary>
    public string? ParentTraceRoot { get; init; }
}
