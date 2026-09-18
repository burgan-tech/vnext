namespace BBT.Workflow.Tasks.Invocation;

/// <summary>
/// Host-level task invocation routing. Bound from configuration section
/// "Workflow:TaskInvocation". Decides whether a task's prepared binding is executed
/// in-process (Orchestration) or shipped to the Execution service.
/// </summary>
/// <remarks>
/// Per-type keys are the wire task-type constants from
/// <see cref="BBT.Workflow.Execution.TaskTypes"/> ("http", "daprservice", "soap",
/// "statestore", "cacheaside"), matched case-insensitively. A type with no local invoker
/// registered resolves to Remote regardless of configuration — see
/// <see cref="TaskInvocationRouter"/>.
/// </remarks>
public sealed class TaskInvocationOptions
{
    public const string SectionName = "Workflow:TaskInvocation";

    /// <summary>
    /// Mode applied when no per-type entry matches. Ships as Remote so enabling the local
    /// path is an explicit, reversible operator decision.
    /// </summary>
    public ExecutionMode DefaultMode { get; set; } = ExecutionMode.Remote;

    /// <summary>
    /// Per-task-type overrides, keyed by wire task type.
    /// </summary>
    public Dictionary<string, ExecutionMode> Modes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
