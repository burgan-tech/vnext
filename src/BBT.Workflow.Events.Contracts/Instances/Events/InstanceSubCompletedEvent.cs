using System.Text.Json;
using BBT.Aether.Events;
using BBT.Workflow.Events;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a SubFlow or SubProcess instance completes.
/// Contains all necessary information about the completed SubItem instance and its data.
/// </summary>
[EventName("instance.sub.completed")]
public class InstanceSubCompletedEvent : IDistributedEvent, ILaneAwareDistributedEvent, ISubflowTerminalEvent
{
    /// <summary>
    /// The ID of the Parent instance
    /// </summary>
    [EventSubject]
    public required Guid InstanceId { get; init; }
    
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
    /// The root ancestor instance ID for nested subflow chains.
    /// <c>null</c> when this is a root (non-subflow) instance.
    /// </summary>
    public Guid? RootInstanceId { get; init; }

    /// <summary>
    /// Whether the completing pipeline chain was executed with a synchronous caller
    /// (sync=true). Carried to the parent so its resume keeps the chain synchronous.
    /// </summary>
    public bool Sync { get; init; }


    /// <summary>W3C traceparent captured at publish time (stamped centrally by the event bus).</summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C tracestate accompanying <see cref="TraceParent"/>.</summary>
    public string? TraceState { get; set; }

    /// <summary>Originating request id (X-Request-Id value) for log correlation.</summary>
    public string? RequestId { get; set; }

    public override string ToString()
    {
        return $"{nameof(InstanceSubCompletedEvent)}: InstanceId={InstanceId} Domain={Domain} Flow={Flow} Version={Version} SubInstanceId={SubInstanceId} CompletedState={CompletedState}";
    }

    /// <summary>
    /// Trace lane anchor of the publishing instance — the PARENT for the parent resume this completion will trigger, so it sits at the
    /// right depth instead of nesting under its predecessor. See <c>WorkflowTraceLane</c>.
    /// </summary>
    public string? TraceRoot { get; set; }

    /// <summary>W3C traceparent of the enclosing lane, so a subflow resume returns to the parent instance's lane.</summary>
    public string? ParentTraceRoot { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? EpisodeStartedAt { get; set; }

    /// <inheritdoc />
    public string? EpisodeTrigger { get; set; }

    /// <inheritdoc />
    public string? EpisodeTransitionKey { get; set; }

    /// <summary>
    /// How many times a terminal-revert has re-published this event as a durable-delivery rearm,
    /// after the original delivery was consumed by the lock-free duplicate ACK and a later
    /// phase-2 resume failure reopened the correlation. <c>null</c>/<c>0</c> for an original
    /// delivery. Capped at a small attempt budget by the publisher.
    /// </summary>
    public int? RearmAttempt { get; init; }
}
