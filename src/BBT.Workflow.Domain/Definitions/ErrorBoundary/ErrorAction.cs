namespace BBT.Workflow.Definitions;

/// <summary>
/// Defines the action to take when an error is caught by an error boundary.
/// </summary>
public enum ErrorAction
{
    /// <summary>
    /// Abort the current execution. Optionally triggers an error transition if configured.
    /// </summary>
    Abort = 0,

    /// <summary>
    /// Retry the failed operation using the configured retry policy.
    /// </summary>
    Retry = 1,

    /// <summary>
    /// Rollback to a compensation state. Typically triggers a rollback transition.
    /// </summary>
    Rollback = 2,

    /// <summary>
    /// Ignore the error and continue execution. Logs the error for audit purposes.
    /// </summary>
    Ignore = 3,

    /// <summary>
    /// Send a notification and optionally transition to a manual review state.
    /// </summary>
    Notify = 4,

    /// <summary>
    /// Log the error only. Does not affect execution flow (treated as Ignore).
    /// </summary>
    Log = 5
}

