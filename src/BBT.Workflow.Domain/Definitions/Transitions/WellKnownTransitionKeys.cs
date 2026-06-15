namespace BBT.Workflow.Definitions;

/// <summary>
/// Contains well-known transition keys that have special meaning in the workflow system.
/// These transitions are resolved differently than regular transitions.
/// </summary>
public static class WellKnownTransitionKeys
{
    /// <summary>
    /// Special transition key for workflow cancellation.
    /// When requested, the system resolves this to the workflow's configured cancel transition.
    /// </summary>
    public const string Cancel = "cancel";

    /// <summary>
    /// Special transition key for workflow data updates.
    /// When requested, the system resolves this to the workflow's configured updateData transition.
    /// </summary>
    public const string UpdateData = "update-parent-data";

    /// <summary>
    /// Special transition key for workflow exit.
    /// When requested, the system resolves this to the workflow's configured exit transition.
    /// </summary>
    public const string Exit = "exit";

    /// <summary>
    /// Virtual transition key used when the workflow timeout fires.
    /// Not a real transition definition — used only to identify the transition record in audit.
    /// </summary>
    public const string Timeout = "$timeout";
}

