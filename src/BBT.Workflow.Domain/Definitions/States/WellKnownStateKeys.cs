namespace BBT.Workflow.Definitions;

/// <summary>
/// Contains well-known state keys that have special meaning in the workflow system.
/// These states are resolved differently than regular states.
/// </summary>
public static class WellKnownStateKeys
{
    public static readonly string[] ReservedTargetKeys = [Self];
    public const string Self = "$self";
}