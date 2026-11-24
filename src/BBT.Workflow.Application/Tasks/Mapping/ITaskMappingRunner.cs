using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Runs a task binding's mapping for one execution: shapes the task before it is invoked (input) and turns its result
/// into the instance-data delta (output). One implementation per mapping kind — a Roslyn <see cref="IMapping"/> script
/// (<see cref="ScriptTaskMappingRunner"/>), the declarative <c>input</c>/<c>output</c> blocks
/// (<see cref="DeclarativeTaskMappingRunner"/>) — behind <see cref="RoutingTaskMappingRunner"/>, which picks per phase.
/// <c>TaskExecutorBase</c> calls it from its template phases, so an executor holds no mapping code of its own.
/// </summary>
public interface ITaskMappingRunner
{
    /// <summary>
    /// Runs the binding's input mapping on <paramref name="task"/> (mutating it, as a script's <c>InputHandler</c> does)
    /// and returns the response kept for the task journal; null when the binding maps no input.
    /// </summary>
    /// <param name="task">The task about to be invoked.</param>
    /// <param name="ctx">The execution; its <see cref="TaskExecutorContext.OnExecuteTask"/> holds the mapping.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="site">
    /// The calling executor's labels and logger, so failure messages and log events read exactly as the executor's own
    /// mapping block produced them. Null uses the defaults for the task's type and logs nothing.
    /// </param>
    Task<Result<ScriptResponse?>> PrepareInputAsync(
        WorkflowTask task, TaskExecutorContext ctx, CancellationToken ct, TaskMappingSite? site = null);

    /// <summary>
    /// Runs the binding's output mapping over <paramref name="result"/> and returns the task's output <c>Data</c> — the
    /// object the execution engine appends to instance data; <c>result.Data</c> unchanged when the binding maps no output.
    /// </summary>
    /// <param name="task">The task that ran.</param>
    /// <param name="result">Its invocation result.</param>
    /// <param name="ctx">The execution.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="site">See <see cref="PrepareInputAsync"/>.</param>
    Task<Result<object?>> ProcessOutputAsync(
        WorkflowTask task, TaskInvocationResult result, TaskExecutorContext ctx, CancellationToken ct,
        TaskMappingSite? site = null);
}

/// <summary>Which runner handles one phase of a binding (see <see cref="TaskMappingRouter"/>).</summary>
public enum TaskMappingKind
{
    /// <summary>The binding maps nothing in this phase: input yields null, output passes <c>result.Data</c> through.</summary>
    PassThrough,

    /// <summary>A Roslyn <see cref="IMapping"/> script (<see cref="OnExecuteTask.Mapping"/> with code).</summary>
    Script,

    /// <summary>The declarative <see cref="OnExecuteTask.Input"/> / <see cref="OnExecuteTask.Output"/> block.</summary>
    Declarative
}

/// <summary>
/// The executor-specific details of a mapping call site: the labels the legacy per-executor mapping blocks put in front
/// of a failure (<c>"Http task input handler failed: "</c>, <c>"Input handler failed for DirectTrigger: "</c>, ...), the
/// logger the failure events go to, which legacy failure events the executor raised, and whether its script mapping
/// went through the per-execution compile memo. Built by <c>TaskExecutorBase</c> from overridable properties.
/// </summary>
/// <param name="Logger">The executor's logger; null logs nothing.</param>
/// <param name="TaskTypeName">The task type written into log events.</param>
/// <param name="InputFailurePrefix">Prefix of a failed script input mapping's message.</param>
/// <param name="OutputFailurePrefix">Prefix of a failed script output mapping's message.</param>
public sealed record TaskMappingSite(
    ILogger? Logger,
    string TaskTypeName,
    string InputFailurePrefix,
    string OutputFailurePrefix)
{
    /// <summary>Whether a failed script input mapping raises <c>TaskInputHandlerFailed</c> (legacy: every executor but CacheAside).</summary>
    public bool LogsScriptInputFailures { get; init; } = true;

    /// <summary>Whether a failed script output mapping raises <c>TaskOutputHandlerFailed</c> (legacy: not CacheAside, not MCP).</summary>
    public bool LogsScriptOutputFailures { get; init; } = true;

    /// <summary>
    /// Whether the script mapping is compiled through the per-execution memo
    /// (<see cref="TaskExecutorContext.CompiledMappingFactories"/>); false compiles through
    /// the engine's <c>CompileToInstanceAsync</c>
    /// once per phase (legacy ExternalHttp behaviour).
    /// </summary>
    public bool MemoizesScriptCompile { get; init; } = true;

    /// <summary>The defaults for a task type: <c>"{type} task input/output handler failed: "</c>, no logger.</summary>
    public static TaskMappingSite For(TaskType taskType) => new(
        null,
        taskType.ToString(),
        $"{taskType} task input handler failed: ",
        $"{taskType} task output handler failed: ");
}
