namespace BBT.Workflow.Instances;

/// <summary>
/// One hop of a correlation-tree walk: which instances of one flow to expand, and how much further
/// the walk may go.
/// </summary>
/// <remarks>
/// <para>
/// Every id belongs to the routed domain and flow. That is what lets a whole level — and, because
/// the far side recurses on its own, a whole cross-domain BRANCH — travel in one call instead of
/// one call per instance per level. Modelled on <c>HumanTaskLeafRequest</c>, which solved the same
/// shape for the human-task descent.
/// </para>
/// </remarks>
public sealed class CorrelationBatchRequest
{
    /// <summary>
    /// Upper bound on ids accepted in one request. An abuse guard for the internal endpoint, which
    /// carries no authorization of its own and therefore cannot trust the caller's batch size.
    /// </summary>
    public const int MaxInstanceIds = 500;

    /// <summary>Instances of the routed flow to expand. Ids that do not resolve are reported, not omitted.</summary>
    public IReadOnlyList<Guid> InstanceIds { get; init; } = [];

    /// <summary>
    /// Levels this request may still descend. Decremented per hop; at zero the walk stops and the
    /// node is returned <c>Resolved = false</c> with reason <c>depth-exceeded</c> rather than being
    /// silently reported as childless.
    /// </summary>
    public int RemainingDepth { get; init; }

    /// <summary>Flow version of the routed instances, when the correlation recorded one.</summary>
    public string? FlowVersion { get; init; }
}

/// <summary>
/// What one instance's expansion produced: its children, recursively, or why they are missing.
/// </summary>
/// <remarks>
/// <see cref="InstanceId"/> is the id that was ASKED about at this level, never a child's. Each
/// level maps the answers back onto the ids it sent, so a caller can always rejoin a hop's result
/// to the nodes it was expanding.
/// </remarks>
public sealed class CorrelationBatchResult
{
    /// <summary>The instance this answer is about, as the requester named it.</summary>
    public Guid InstanceId { get; init; }

    /// <summary>
    /// False when the subtree could not be walked to the end — the depth bound, an unreachable
    /// partner domain, or a missing instance row. Distinct from an empty <see cref="Children"/>
    /// list on a resolved node, which genuinely means "no children".
    /// </summary>
    public bool Resolved { get; init; }

    /// <summary>Why the expansion is incomplete: <c>depth-exceeded</c>, <c>hop-failed</c>, <c>instance-missing</c>.</summary>
    public string? UnresolvedReason { get; init; }

    /// <summary>This instance's correlated children, already expanded recursively.</summary>
    public IReadOnlyList<InstanceCorrelationNode> Children { get; init; } = [];

    // ---- Self-description of the requested instance -------------------------------------------
    //
    // A hop reports what it knows about the instances it was ASKED about, not only about their
    // children. That is what fixes the cross-domain blind spot: the parent builds a child's node
    // from its own correlation row — which carries the link (parentState, correlationId, bubbled
    // state) but nothing live about the instance — and only the domain that OWNS the instance can
    // read its row. Before this, a child in another domain came back with a null key, a null
    // ownState and a status derived from the link, and nothing said so.

    /// <summary>The instance's business key, read in the domain that owns it. Null when the row is missing.</summary>
    public string? Key { get; init; }

    /// <summary>Where this instance itself is, as opposed to the state its correlation bubbled upward.</summary>
    public string? OwnState { get; init; }

    /// <summary>The instance's live status, read from its own row rather than inferred from the link.</summary>
    public InstanceStatus? Status { get; init; }

    /// <summary>The instance's flow version as recorded on its row.</summary>
    public string? FlowVersion { get; init; }
}
