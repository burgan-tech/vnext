namespace BBT.Workflow.Scripting.Rules;

/// <summary>
/// Transition definition subset for Dynamic Expresso rules (no script bodies or task graphs).
/// </summary>
public sealed class ExpressoTransitionView
{
    /// <summary>Transition key.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Source state key, if any.</summary>
    public string? From { get; init; }

    /// <summary>Target state key.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary><see cref="BBT.Workflow.Definitions.TriggerType"/> as string.</summary>
    public string TriggerType { get; init; } = string.Empty;

    /// <summary><see cref="BBT.Workflow.Definitions.TransitionKind"/> when set; otherwise null.</summary>
    public string? TriggerKind { get; init; }
}
