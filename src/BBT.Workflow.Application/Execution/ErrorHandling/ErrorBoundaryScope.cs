namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Defines the scope/level where an error was caught or is being handled.
/// These scopes represent the error boundary hierarchy in the workflow engine.
/// </summary>
/// <remarks>
/// Error Boundary Hierarchy:
/// - Task: Individual task execution errors (most specific)
/// - State: State-level errors including onEntry/onExit tasks and pipeline step failures
/// - SubFlow: Errors propagated from child workflows to parent
/// - Global: Workflow-level error handling (most general, covers entire execution)
/// 
/// Error policy resolution follows: Task -> State -> Global
/// </remarks>
public enum ErrorBoundaryScope
{
    /// <summary>
    /// Error occurred at the task level during individual task execution.
    /// This is the most specific boundary.
    /// </summary>
    Task = 0,

    /// <summary>
    /// Error occurred at the state level.
    /// Includes onEntry/onExit task errors and transition pipeline step failures.
    /// </summary>
    State = 1,

    /// <summary>
    /// Error is being handled at the global workflow level.
    /// This is the most general boundary, covering the entire workflow execution.
    /// </summary>
    Global = 2,

    /// <summary>
    /// Error occurred in a SubFlow and is being propagated to parent workflow.
    /// </summary>
    SubFlow = 3
}

