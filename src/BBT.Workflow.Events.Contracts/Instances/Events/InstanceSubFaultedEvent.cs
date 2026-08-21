using System.Text.Json;
using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Events;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a SubFlow instance faults, notifying the parent instance.
/// Contains incident information from the faulted SubFlow for parent-level recording.
/// </summary>
/// <remarks>
/// This event supports hooks. Register hooks via DI:
/// <code>
/// services.AddEventHook&lt;InstanceSubFaultedEvent, InstanceSubFaultedEventHook&gt;();
/// </code>
/// </remarks>
[EventHook(EventHookMode.DurablePostCommit)]
[EventName("instance.sub.faulted")]
public class InstanceSubFaultedEvent : IDistributedEvent, ILaneAwareDistributedEvent
{
    /// <summary>
    /// The ID of the Parent instance
    /// </summary>
    [EventSubject]
    public required Guid InstanceId { get; init; }

    /// <summary>
    /// The domain of the parent workflow
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The workflow name of the parent
    /// </summary>
    public required string Flow { get; init; }

    /// <summary>
    /// The version of the parent workflow
    /// </summary>
    public required string? Version { get; init; }

    /// <summary>
    /// The ID of the faulted SubFlow instance
    /// </summary>
    public required Guid SubInstanceId { get; init; }

    /// <summary>
    /// The state the SubFlow was in when it faulted
    /// </summary>
    public required string FaultedState { get; init; }

    /// <summary>
    /// The state type the SubFlow was in when it faulted.
    /// </summary>
    public int? FaultedStateType { get; init; }

    /// <summary>
    /// The state subtype the SubFlow was in when it faulted.
    /// </summary>
    public int? FaultedStateSubType { get; init; }

    /// <summary>
    /// The latest instance data of the faulted SubFlow.
    /// </summary>
    public JsonElement? InstanceData { get; init; }

    /// <summary>
    /// When the SubFlow faulted
    /// </summary>
    public required DateTime FaultedAt { get; init; }

    /// <summary>
    /// The SubFlow's workflow name (for incident message context)
    /// </summary>
    public string? SubFlowName { get; init; }

    /// <summary>
    /// Error message from the SubFlow's active incident
    /// </summary>
    public string? IncidentMessage { get; init; }

    /// <summary>
    /// Error code from the SubFlow's active incident
    /// </summary>
    public string? IncidentErrorCode { get; init; }

    /// <summary>
    /// Error layer from the SubFlow's active incident
    /// </summary>
    public string? IncidentErrorLayer { get; init; }

    /// <summary>
    /// Exception stack trace from the SubFlow's active incident, when available.
    /// </summary>
    public string? IncidentStackTrace { get; init; }

    /// <summary>
    /// HTTP status code from the SubFlow's active incident, when available.
    /// </summary>
    public int? IncidentStatusCode { get; init; }

    /// <summary>
    /// OpenTelemetry trace ID from the SubFlow's active incident
    /// </summary>
    public string? IncidentTraceId { get; init; }

    /// <summary>
    /// Task key from the SubFlow's active incident
    /// </summary>
    public string? IncidentTaskKey { get; init; }

    /// <summary>
    /// Transition where the error occurred in the SubFlow
    /// </summary>
    public string? IncidentTransition { get; init; }

    /// <summary>
    /// State where the error occurred in the SubFlow
    /// </summary>
    public string? IncidentState { get; init; }

    /// <summary>
    /// Boundary action taken in the SubFlow (if any)
    /// </summary>
    public string? IncidentBoundaryAction { get; init; }

    /// <summary>
    /// Boundary level that matched in the SubFlow (if any)
    /// </summary>
    public string? IncidentBoundaryLevel { get; init; }

    /// <summary>
    /// The root ancestor instance ID for nested subflow chains.
    /// <c>null</c> when this is a root (non-subflow) instance.
    /// </summary>
    public Guid? RootInstanceId { get; init; }

    /// <summary>
    /// Whether the faulting pipeline chain was executed with a synchronous caller
    /// (sync=true). Carried to the parent so its error-boundary resume keeps the chain synchronous.
    /// </summary>
    public bool Sync { get; init; }

    /// <summary>
    /// The kind of faulted SubItem. A missing value represents a legacy SubFlow event.
    /// </summary>
    public SubItemType? SubItemType { get; init; }

    /// <summary>The origin of this terminal operation, when provided by the publisher.</summary>
    public TerminationOrigin? TerminationOrigin { get; init; }

    /// <summary>The instance that initiated the terminal cascade, when available.</summary>
    public Guid? InitiatorInstanceId { get; init; }

    /// <summary>The identifier shared by the terminal cascade, when available.</summary>
    public Guid? CascadeId { get; init; }


    /// <summary>W3C traceparent captured at publish time (stamped centrally by the event bus).</summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C tracestate accompanying <see cref="TraceParent"/>.</summary>
    public string? TraceState { get; set; }

    /// <summary>Originating request id (X-Request-Id value) for log correlation.</summary>
    public string? RequestId { get; set; }

    public override string ToString()
    {
        return $"{nameof(InstanceSubFaultedEvent)}: InstanceId={InstanceId} Domain={Domain} Flow={Flow} Version={Version} SubInstanceId={SubInstanceId} FaultedState={FaultedState}";
    }

    /// <summary>
    /// Trace lane anchor of the publishing instance — the PARENT for the parent resume this fault will trigger, so it sits at the
    /// right depth instead of nesting under its predecessor. See <c>WorkflowTraceLane</c>.
    /// </summary>
    public string? TraceRoot { get; set; }

    /// <summary>W3C traceparent of the enclosing lane, so a subflow resume returns to the parent instance's lane.</summary>
    public string? ParentTraceRoot { get; set; }
}
