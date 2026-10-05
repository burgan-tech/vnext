namespace BBT.Workflow.Definitions;

/// <summary>
/// Contains well-known state keys that have special meaning in the workflow system.
/// These states are resolved differently than regular states.
/// </summary>
public static class WellKnownStateKeys
{
    public static readonly string[] ReservedTargetKeys = [Self];
    public const string Self = "$self";

    /// <summary>
    /// Runtime-owned source of the start transition for a workflow that declares no Initial state:
    /// the instance is born here and the start transition moves it to <c>startTransition.target</c>.
    /// Never declared, never a target, never re-entered — deliberately NOT in
    /// <see cref="ReservedTargetKeys"/>, which means "resolve to the current state".
    /// </summary>
    public const string Start = "$start";
}