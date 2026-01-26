using BBT.Aether;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Instance Correlation
/// </summary>
public sealed class InstanceCorrelation : Entity<Guid>
{
    private InstanceCorrelation()
    {
    }

    private InstanceCorrelation(
        Guid id,
        Guid instanceId,
        string parentState,
        Guid subFlowInstanceId,
        string subFlowType,
        string subFlowDomain,
        string subFlowName,
        string? subFlowVersion
    ) : base(id)
    {
        ParentInstanceId = instanceId;
        ParentState = Check.NotNullOrWhiteSpace(parentState, nameof(parentState), StateConstants.MaxKeyLength);
        SubFlowInstanceId = subFlowInstanceId;
        SubFlowType = SubFlowType.FromCode(subFlowType);
        IsCompleted = false;
        SubFlowDomain = Check.NotNullOrWhiteSpace(subFlowDomain, nameof(subFlowDomain), WorkflowConstants.MaxDomainLength);
        SubFlowName = Check.NotNullOrWhiteSpace(subFlowName, nameof(subFlowName), WorkflowConstants.MaxKeyLength);
        SubFlowVersion = Check.Length(subFlowVersion, nameof(subFlowVersion), WorkflowConstants.MaxVersionLength);
    }

    public static InstanceCorrelation Create(
        Guid id,
        Guid instanceId,
        string parentState,
        Guid subFlowInstanceId,
        string subFlowType,
        string subFlowDomain,
        string subFlowName,
        string? subFlowVersion)
    {
        return new InstanceCorrelation(id, instanceId, parentState, subFlowInstanceId, subFlowType, subFlowDomain, subFlowName, subFlowVersion);
    }

    /// <summary>
    /// <see cref="Instance"/> Parent Instance ID
    /// </summary>
    public Guid ParentInstanceId { get; private set; }

    /// <summary>
    /// <see cref="State"/> Parent State ID
    /// </summary>
    public string ParentState { get; private set; }

    /// <summary>
    /// <see cref="Instance"/> SubFlow Instance ID
    /// </summary>
    public Guid SubFlowInstanceId { get; private set; }

    /// <summary>
    /// Sub Flow Domain
    /// </summary>
    public string SubFlowDomain { get; private set; }

    /// <summary>
    /// Sub Flow Name
    /// </summary>
    public string SubFlowName { get; private set; }

    /// <summary>
    /// Sub Flow Version
    /// </summary>
    public string? SubFlowVersion { get; private set; }

    /// <summary>
    /// SubFlow Type: "S" (SubFlow - blocking) or "P" (SubProcess - non-blocking)
    /// This field enables performant querying without joining to Instance table.
    /// </summary>
    public SubFlowType SubFlowType { get; private set; }

    /// <summary>
    /// SubFlow's current state - updated when SubFlow state changes.
    /// Enables parent to track SubFlow progress without cross-domain queries.
    /// </summary>
    public string? SubFlowCurrentState { get; private set; }

    /// <summary>
    /// Timestamp when SubFlowCurrentState was last updated.
    /// Used for out-of-order event detection to prevent stale events from overwriting recent states.
    /// </summary>
    public DateTime? SubFlowStateChangedAt { get; private set; }

    /// <summary>
    /// Is completed
    /// </summary>
    public bool IsCompleted { get; private set; }

    /// <summary>
    /// Completed at
    /// </summary>
    public DateTime? CompletedAt { get; private set; }

    public void Completed()
    {
        IsCompleted = true;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reverts the correlation to its incomplete state.
    /// Used for rollback scenarios when subflow completion needs to be undone.
    /// </summary>
    public void Revert()
    {
        IsCompleted = false;
        CompletedAt = null;
    }

    /// <summary>
    /// Updates the SubFlow's current state.
    /// Called when SubFlow state changes to keep parent informed.
    /// </summary>
    /// <param name="newState">The new state of the SubFlow</param>
    /// <param name="changedAt">Timestamp when the state change occurred</param>
    public void UpdateSubFlowState(string newState, DateTime changedAt)
    {
        SubFlowCurrentState = Check.Length(newState, nameof(newState), StateConstants.MaxKeyLength);
        SubFlowStateChangedAt = changedAt;
    }

    internal InstanceCorrelation CreateSnapshot()
    {
        var snapshot = new InstanceCorrelation
        {
            Id = Id,
            ParentInstanceId = ParentInstanceId,
            ParentState = ParentState,
            SubFlowInstanceId = SubFlowInstanceId,
            SubFlowDomain = SubFlowDomain,
            SubFlowName = SubFlowName,
            SubFlowVersion = SubFlowVersion,
            SubFlowType = SubFlowType,
            SubFlowCurrentState = SubFlowCurrentState,
            SubFlowStateChangedAt = SubFlowStateChangedAt,
            IsCompleted = IsCompleted,
            CompletedAt = CompletedAt
        };

        return snapshot;
    }
}
