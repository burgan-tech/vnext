namespace BBT.Workflow.Scripting.Rules;

/// <summary>
/// Read-only instance subset for rule expressions (latest data payload + common scalars).
/// </summary>
public sealed class ExpressoInstanceView
{
    /// <summary>Instance identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Optional business key.</summary>
    public string? Key { get; init; }

    /// <summary>Flow key.</summary>
    public string Flow { get; init; } = string.Empty;

    /// <summary>Flow version the instance was started with (e.g. for versioned cache keys).</summary>
    public string? Version { get; init; }

    /// <summary>Engine current state key.</summary>
    public string? CurrentState { get; init; }

    /// <summary>Externally visible state key.</summary>
    public string? EffectiveState { get; init; }

    /// <summary>Effective state type name.</summary>
    public string? EffectiveStateType { get; init; }

    /// <summary>Effective state subtype name.</summary>
    public string? EffectiveStateSubType { get; init; }

    /// <summary>
    /// The status code a client observes for this instance ("A", "B", "C", "F", "P") — the deepest
    /// active SubFlow's status when one is running, otherwise the instance's own. The status
    /// counterpart of <see cref="EffectiveState"/>; same value as <c>metadata.effectiveStatus</c>.
    /// </summary>
    public string? EffectiveStatus { get; init; }

    /// <summary>
    /// How this instance was STARTED: <c>"R"</c> root, <c>"S"</c> SubFlow child, <c>"P"</c>
    /// SubProcess child. Immutable, so a rule can branch on the origin without re-deriving it —
    /// <c>instance.Type == "S"</c>.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>Latest instance data payload (JSON object/array root).</summary>
    public RuleJsonDynamic Data { get; init; } = RuleJsonDynamic.Empty;
}
