namespace BBT.Workflow.Instances.HumanTask;

/// <summary>
/// One level's descent request: which instances of one flow to resolve, on whose behalf, and how
/// much further the walk may go.
/// </summary>
/// <remarks>
/// All ids belong to the routed domain and flow, which is what lets a whole level cross a domain
/// boundary in one call instead of one call per instance per level.
/// </remarks>
public sealed class HumanTaskLeafRequest
{
    /// <summary>
    /// Upper bound on ids accepted in one request. An abuse guard for the internal endpoint, which
    /// carries no authorization and therefore cannot trust the caller's batch size; the real bound
    /// is the per-schema limit in the calling runtime.
    /// </summary>
    public const int MaxInstanceIds = 500;

    /// <summary>Instances of the routed flow to resolve. Ids that do not resolve are reported, not omitted.</summary>
    public IReadOnlyList<Guid> InstanceIds { get; init; } = [];

    /// <summary>
    /// The caller's resolved roles. Authorization must happen in the domain that owns the leaf,
    /// because only that runtime can resolve the leaf's workflow definition — so the roles travel
    /// with the request rather than the decision travelling back.
    /// </summary>
    public IReadOnlyList<string> CallerRoles { get; init; } = [];

    /// <summary>
    /// Headers backing the <c>$.context.Headers.*</c> namespace dynamic role grants read. A grant
    /// evaluated without them does not fail closed — it silently cannot match — so they must follow
    /// the roles across the boundary.
    /// </summary>
    public Dictionary<string, string?> Headers { get; init; } = [];

    /// <summary>
    /// Levels this request may still descend. Decremented per hop; at zero the walk stops and
    /// reports the instance as unresolved rather than recursing further.
    /// </summary>
    public int RemainingDepth { get; init; }

    /// <summary>Flow version of the routed instances, when the correlation recorded one.</summary>
    public string? FlowVersion { get; init; }
}

/// <summary>
/// What one instance's descent produced: whether the caller may act on the leaf, and the leaf's own
/// human-task text.
/// </summary>
/// <remarks>
/// <see cref="InstanceId"/> is the id that was ASKED about at this level, never the leaf's — each
/// level maps its children's answers back onto its own ids, so the original root keeps its identity
/// all the way up. The client knows the root; it does not know the subflow.
/// </remarks>
public sealed class HumanTaskLeafResult
{
    /// <summary>The instance this answer is about, as the requester named it.</summary>
    public Guid InstanceId { get; init; }

    /// <summary>
    /// False when the descent could not complete — an unresolvable definition or state, or the
    /// depth bound. Distinct from an unauthorized answer: one means the list is incomplete, the
    /// other means there is correctly nothing here for this caller.
    /// </summary>
    public bool Resolved { get; init; }

    /// <summary>Whether the caller may trigger at least one user transition on the leaf.</summary>
    public bool Authorized { get; init; }

    /// <summary>The leaf's <c>humanTask.title</c>, read from the leaf's own latest data.</summary>
    public string? Title { get; init; }

    /// <summary>The leaf's <c>humanTask.description</c>.</summary>
    public string? Description { get; init; }

    /// <summary>Domain that owns the leaf — the caller's own when the chain never left it.</summary>
    public string? LeafDomain { get; init; }

    /// <summary>Workflow key of the leaf.</summary>
    public string? LeafFlow { get; init; }

    /// <summary>The leaf's current state key.</summary>
    public string? LeafState { get; init; }

    /// <summary>Why the descent stopped, when <see cref="Resolved"/> is false. Never customer data.</summary>
    public string? DropReason { get; init; }

    internal static HumanTaskLeafResult Dropped(Guid instanceId, string reason) =>
        new() { InstanceId = instanceId, Resolved = false, DropReason = reason };
}
