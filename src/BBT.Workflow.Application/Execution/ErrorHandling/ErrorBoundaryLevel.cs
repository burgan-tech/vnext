namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Defines the level in the hierarchy where an error boundary was found.
/// Used for logging and determining which boundary handled an error.
/// </summary>
public enum ErrorBoundaryLevel
{
    /// <summary>
    /// Error boundary defined at the task level.
    /// Has highest priority in the resolution hierarchy.
    /// </summary>
    Task = 0,

    /// <summary>
    /// Error boundary defined at the state level.
    /// Evaluated when no task-level boundary handles the error.
    /// </summary>
    State = 1,

    /// <summary>
    /// Error boundary defined at the global workflow level.
    /// Evaluated when no task or state-level boundary handles the error.
    /// </summary>
    Global = 2,

    /// <summary>
    /// Error boundary for SubFlow propagation.
    /// Applied when a child workflow error propagates to parent.
    /// </summary>
    SubFlow = 3
}

