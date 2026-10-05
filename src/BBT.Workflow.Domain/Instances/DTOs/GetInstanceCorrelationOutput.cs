using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Output for the instance-correlation tree - the instance and, recursively, its correlated child
/// subflow/subprocess instances, walked parent -> child.
/// </summary>
public sealed class GetInstanceCorrelationOutput
{
    /// <summary>
    /// Root node of the correlation tree (the requested instance).
    /// </summary>
    public InstanceCorrelationNode Root { get; set; } = new();
}

/// <summary>
/// A node in the instance-correlation tree representing one instance.
/// </summary>
public sealed class InstanceCorrelationNode
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
    /// Current state key. For a child this is the correlation's tracked state, which reports the
    /// DEEPEST active descendant — so a child that itself has an active subflow shows the grandchild's
    /// state here. Use <see cref="OwnState"/> when you need where this node itself is.
    /// </summary>
    public string? CurrentState { get; set; }

    /// <summary>
    /// The state of THIS instance itself, independent of any descendant. Drawn from the instance row
    /// rather than the correlation's bubbled-up state, so a tree/graph can place each node where it
    /// actually is. Null when the child instance could not be read (for example a cross-domain child).
    /// </summary>
    public string? OwnState { get; set; }

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
    /// Identifier of the correlation row that links this node to its parent — the handle for
    /// addressing the LINK itself (as opposed to <see cref="Id"/>, which addresses the instance).
    /// Null on the root, which is nobody's correlated child.
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// When the correlation was created — i.e. when this child was spawned. Lets a caller order
    /// siblings and show how long a child has been attached. Null on the root.
    /// </summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// How the link ended: Completed, Faulted or Canceled. <see cref="IsCompleted"/> only says
    /// *whether* it ended, so this is what distinguishes a healthy child from a failed one. Null on
    /// the root and while the correlation is still open.
    /// </summary>
    public SubItemTerminalOutcome? TerminalOutcome { get; set; }

    /// <summary>
    /// When the child's tracked state last moved — "sitting here since". Null on the root.
    /// </summary>
    public DateTime? StateChangedAt { get; set; }

    /// <summary>
    /// Link to this node's own instance resource, so a caller rendering the tree can navigate to any
    /// node without composing URLs itself.
    /// </summary>
    public string? Href { get; set; }

    /// <summary>
    /// Child subflow/subprocess instances.
    /// </summary>
    public List<InstanceCorrelationNode> Children { get; set; } = [];

    /// <summary>
    /// Whether this node's OWN SUBTREE was walked to the end. <c>false</c> means
    /// <see cref="Children"/> is incomplete or empty for a reason other than "there are none" —
    /// see <see cref="UnresolvedReason"/>.
    /// </summary>
    /// <remarks>
    /// The node itself is always real; only its descendants are in question. Without this a client
    /// cannot tell a genuine leaf from a partner domain that was unreachable, and the two are very
    /// different answers — which is exactly how the pre-batch implementation misled callers about
    /// cross-domain children. A tree whose nodes are all <c>true</c> is complete.
    /// </remarks>
    public bool Resolved { get; set; } = true;

    /// <summary>
    /// Why the subtree is incomplete, when <see cref="Resolved"/> is false: <c>depth-exceeded</c>,
    /// <c>hop-failed</c>, or <c>instance-missing</c>. Omitted on a fully walked node.
    /// </summary>
    public string? UnresolvedReason { get; set; }
}
