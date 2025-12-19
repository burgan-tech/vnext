namespace BBT.Workflow.Definitions;

/// <summary>
/// Trigger types that can initiate a workflow transition.
/// </summary>
public enum TriggerType
{
    /// <summary>
    /// Manual trigger - initiated by user or external API call.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Automatic trigger - initiated by system based on conditions or rules.
    /// </summary>
    Automatic = 1,

    /// <summary>
    /// Scheduled trigger - initiated by timer or cron expression.
    /// </summary>
    Scheduled = 2,

    /// <summary>
    /// Event trigger - initiated by external event or message.
    /// </summary>
    Event = 3
}

/// <summary>
/// Defines special behavior kinds for transitions.
/// </summary>
public enum TransitionKind
{
    NA = 0,
    /// <summary>
    /// Default automatic transition - used as fallback when no other automatic transition conditions are satisfied.
    /// This transition is evaluated last and only executed if no regular automatic transitions matched.
    /// </summary>
    DefaultAutoTransition = 10
}