using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Context for task execution containing all necessary information
/// for an executor to process a task.
/// </summary>
/// <param name="Task">The workflow task to execute.</param>
/// <param name="OnExecuteTask">The on-execute task definition containing mapping configuration.</param>
/// <param name="ScriptContext">The script context for variable resolution and script execution.</param>
/// <param name="InstanceTransitionId">The instance transition ID for tracking (optional).</param>
/// <param name="TaskTrigger">The trigger that initiated this task execution.</param>
public sealed record TaskExecutorContext(
    WorkflowTask Task,
    OnExecuteTask OnExecuteTask,
    ScriptContext ScriptContext,
    Guid? InstanceTransitionId,
    TaskTrigger TaskTrigger)
{
    /// <summary>
    /// Gets the task type from the workflow task.
    /// </summary>
    public TaskType TaskType => Task.GetTaskType();

    /// <summary>
    /// Holds the input handler response for auditing purposes.
    /// </summary>
    public ScriptResponse? InputResponse { get; set; }

    /// <summary>
    /// Holds the raw invocation result as serialized JSON, captured after InvokeAsync
    /// and before output mapping (ProcessOutputAsync). Null if invocation never ran.
    /// </summary>
    public string? RawInvocationResultJson { get; set; }
}

