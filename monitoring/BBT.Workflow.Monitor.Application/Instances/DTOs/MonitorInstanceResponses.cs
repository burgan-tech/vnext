using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Monitor.Instances.DTOs;

/// <summary>
/// Lightweight, extension-free read model for a workflow instance.
/// Intended for dashboard and list views in vnext-forge.
/// </summary>
public sealed class MonitorInstanceResponse
{
    /// <summary>Instance unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Business key of the instance.</summary>
    public string? Key { get; set; }

    /// <summary>Flow (workflow) key.</summary>
    public string? Flow { get; set; }

    /// <summary>Flow version at start.</summary>
    public string? FlowVersion { get; set; }

    /// <summary>Domain the instance belongs to.</summary>
    public string? Domain { get; set; }

    /// <summary>Tags assigned to the instance.</summary>
    public List<string>? Tags { get; set; }

    /// <summary>Instance lifecycle metadata.</summary>
    public MonitorInstanceMetadata? Metadata { get; set; }

    /// <summary>Active child correlations (sub-flows, sub-processes).</summary>
    public List<MonitorCorrelationInfo>? ActiveCorrelations { get; set; }
}

/// <summary>
/// State, audit, and lifecycle metadata for a monitor instance response.
/// </summary>
public sealed class MonitorInstanceMetadata
{
    /// <summary>Current internal state key.</summary>
    public string? CurrentState { get; set; }

    /// <summary>Effective state exposed to callers.</summary>
    public string? EffectiveState { get; set; }

    /// <summary>Instance status (Active, Completed, Faulted, etc.).</summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>Type of the effective state.</summary>
    public StateType? EffectiveStateType { get; set; }

    /// <summary>Subtype of the effective state.</summary>
    public StateSubType? EffectiveStateSubType { get; set; }

    /// <summary>When the instance completed. Null if still active.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Total duration in seconds from creation to completion.</summary>
    public double? Duration { get; set; }

    /// <summary>When the instance was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the instance was last modified.</summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>Creator user identifier.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Creator behalf-of user identifier.</summary>
    public string? CreatedByBehalfOf { get; set; }

    /// <summary>Modifier user identifier.</summary>
    public string? ModifiedBy { get; set; }

    /// <summary>Modifier behalf-of user identifier.</summary>
    public string? ModifiedByBehalfOf { get; set; }
}

/// <summary>
/// Snapshot of an active correlation (sub-flow or sub-process).
/// </summary>
public sealed class MonitorCorrelationInfo
{
    /// <summary>Correlation unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Parent state where the sub-flow was triggered.</summary>
    public string? ParentState { get; set; }

    /// <summary>Sub-flow instance ID.</summary>
    public Guid SubFlowInstanceId { get; set; }

    /// <summary>Sub-flow domain.</summary>
    public string? SubFlowDomain { get; set; }

    /// <summary>Sub-flow name.</summary>
    public string? SubFlowName { get; set; }

    /// <summary>Sub-flow version.</summary>
    public string? SubFlowVersion { get; set; }

    /// <summary>Sub-flow type code (S = SubFlow, P = SubProcess).</summary>
    public string? SubFlowType { get; set; }

    /// <summary>Current state of the sub-flow instance.</summary>
    public string? SubFlowCurrentState { get; set; }
}

/// <summary>
/// Response for instance data: latest attributes plus version history.
/// </summary>
public sealed class MonitorInstanceDataResponse
{
    /// <summary>The most recent instance data attributes.</summary>
    public JsonElement? LatestData { get; set; }

    /// <summary>Ordered history of all data versions, oldest first.</summary>
    public List<MonitorDataVersion> VersionHistory { get; set; } = [];
}

/// <summary>
/// A single version entry in the instance data history.
/// </summary>
public sealed class MonitorDataVersion
{
    /// <summary>Version identifier (semantic).</summary>
    public string? Version { get; set; }

    /// <summary>When this version was entered.</summary>
    public DateTime? EnteredAt { get; set; }

    /// <summary>Attributes at this version.</summary>
    public JsonElement? Data { get; set; }
}

/// <summary>
/// Response for instance state-transition history.
/// Provides the data to draw the state-flow graph.
/// </summary>
public sealed class MonitorInstanceHistoryResponse
{
    /// <summary>Ordered list of transitions, oldest first.</summary>
    public List<MonitorTransitionItem> Transitions { get; set; } = [];
}

/// <summary>
/// A single state-transition record for the instance flow graph.
/// </summary>
public sealed class MonitorTransitionItem
{
    /// <summary>Transition unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The transition definition key.</summary>
    public string? TransitionId { get; set; }

    /// <summary>State the instance was in before this transition.</summary>
    public string? FromState { get; set; }

    /// <summary>State the instance moved to. Null if transition is still in progress.</summary>
    public string? ToState { get; set; }

    /// <summary>When this transition started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When this transition completed. Null if still in progress.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Duration of this transition in seconds. Null if still in progress.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>How the transition was triggered (Manual, Auto, Timer, etc.).</summary>
    public TriggerType TriggerType { get; set; }

    /// <summary>User who triggered the transition.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Behalf-of user who triggered the transition.</summary>
    public string? CreatedByBehalfOf { get; set; }
}

/// <summary>
/// Task execution record for a specific transition.
/// </summary>
public sealed class MonitorInstanceTaskResponse
{
    /// <summary>Task entity unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The transition this task belongs to.</summary>
    public Guid TransitionId { get; set; }

    /// <summary>The task definition key.</summary>
    public string? TaskId { get; set; }

    /// <summary>Platform/infrastructure execution status.</summary>
    public string? Status { get; set; }

    /// <summary>Business-level outcome status.</summary>
    public string? BusinessStatus { get; set; }

    /// <summary>When the task started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When the task finished. Null if still running.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Duration of the task in seconds.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Request payload sent to the task.</summary>
    public JsonElement? Request { get; set; }

    /// <summary>Response payload received from the task.</summary>
    public JsonElement? Response { get; set; }
}
