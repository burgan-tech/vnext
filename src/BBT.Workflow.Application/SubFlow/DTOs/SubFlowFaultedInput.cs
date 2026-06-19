using System.Text.Json;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Data payload for SubFlow fault propagation event (upward: child faulted, notify parent).
/// Contains incident information from the faulted SubFlow for parent-level recording.
/// </summary>
public record SubFlowFaultedInput
{
    /// <summary>
    /// The ID of the parent instance to notify
    /// </summary>
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
}
