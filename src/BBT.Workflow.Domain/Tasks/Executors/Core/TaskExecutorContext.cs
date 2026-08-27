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
/// <param name="Origin">The component that initiated this execution (Flow, Extension, Function, ...).</param>
public sealed record TaskExecutorContext(
    WorkflowTask Task,
    OnExecuteTask OnExecuteTask,
    ScriptContext ScriptContext,
    Guid? InstanceTransitionId,
    TaskTrigger TaskTrigger,
    TaskExecutionOrigin Origin)
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
    /// Holds the raw invocation result captured after InvokeAsync and before output mapping
    /// (ProcessOutputAsync), already materialized in its journal representation. Null if invocation
    /// never ran. Keeping JsonData instead of the original object graph avoids extending the graph's
    /// lifetime through output processing while preserving exactly one serialization.
    /// </summary>
    public JsonData? RawInvocationResult { get; set; }

    /// <summary>
    /// Per-execution compiled-mapping factory memo, keyed by (mapping, target type) — see
    /// <c>TaskExecutorBase.GetOrCompileMappingAsync</c>. Boxed as <see cref="object"/> because a
    /// record cannot declare a dictionary whose value type varies by the generic <c>T</c> callers
    /// ask for; each entry is actually a <c>Func&lt;T&gt;</c> for that entry's target type.
    /// </summary>
    public Dictionary<(ScriptCode Mapping, Type Target), object>? CompiledMappingFactories { get; set; }
}
