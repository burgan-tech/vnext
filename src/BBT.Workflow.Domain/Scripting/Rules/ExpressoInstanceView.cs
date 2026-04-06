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

    /// <summary>Engine current state key.</summary>
    public string? CurrentState { get; init; }

    /// <summary>Externally visible state key.</summary>
    public string? EffectiveState { get; init; }

    /// <summary>Effective state type name.</summary>
    public string? EffectiveStateType { get; init; }

    /// <summary>Effective state subtype name.</summary>
    public string? EffectiveStateSubType { get; init; }

    /// <summary>Latest instance data payload (JSON object/array root).</summary>
    public RuleJsonDynamic Data { get; init; } = RuleJsonDynamic.Empty;
}
