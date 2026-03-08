using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Output for instance hierarchy - recursive tree of instance and child subflow/subprocess instances.
/// </summary>
public sealed class GetInstanceHierarchyOutput
{
    /// <summary>
    /// Root node of the hierarchy tree (the requested instance).
    /// </summary>
    public InstanceHierarchyNode Root { get; set; } = new();
}

/// <summary>
/// A node in the instance hierarchy tree representing one instance.
/// </summary>
public sealed class InstanceHierarchyNode
{
    /// <summary>
    /// Instance ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Instance key (human-readable identifier).
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Flow (workflow) name.
    /// </summary>
    public string Flow { get; set; } = string.Empty;

    /// <summary>
    /// Domain.
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Flow version.
    /// </summary>
    public string? FlowVersion { get; set; }

    /// <summary>
    /// Current state key.
    /// </summary>
    public string? CurrentState { get; set; }

    /// <summary>
    /// Instance status.
    /// </summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>
    /// SubFlow type: SubFlow (blocking) or SubProcess (non-blocking). Null for root instance.
    /// </summary>
    public SubFlowType? SubFlowType { get; set; }

    /// <summary>
    /// Whether the subflow/subprocess correlation is completed.
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// When the subflow/subprocess completed. Null if not completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// State in parent from which this subflow was started.
    /// </summary>
    public string? ParentState { get; set; }

    /// <summary>
    /// Child subflow/subprocess instances.
    /// </summary>
    public List<InstanceHierarchyNode> Children { get; set; } = [];
}
