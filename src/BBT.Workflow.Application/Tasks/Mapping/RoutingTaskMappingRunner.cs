using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Tasks.Executors;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Picks the runner for one phase of a binding, the same way <c>RoutingConditionEvaluator</c> picks a condition
/// evaluator: the declarative block of that phase when the binding authors one, else the Roslyn script when
/// <see cref="OnExecuteTask.Mapping"/> has code, else pass-through (input → null, output → <c>result.Data</c>, exactly
/// what an executor did without a mapping). Publish rejects a binding that carries both a script and blocks (D-4); if one
/// reaches the runtime anyway, the blocks win.
/// </summary>
public static class TaskMappingRouter
{
    /// <summary>The kind that handles <paramref name="binding"/>'s input phase.</summary>
    public static TaskMappingKind InputKind(OnExecuteTask binding) =>
        binding.HasDeclarativeMapping
            ? binding.Input is null ? TaskMappingKind.PassThrough : TaskMappingKind.Declarative
            : ScriptOrPassThrough(binding);

    /// <summary>The kind that handles <paramref name="binding"/>'s output phase.</summary>
    public static TaskMappingKind OutputKind(OnExecuteTask binding) =>
        binding.HasDeclarativeMapping
            ? binding.Output is null ? TaskMappingKind.PassThrough : TaskMappingKind.Declarative
            : ScriptOrPassThrough(binding);

    private static TaskMappingKind ScriptOrPassThrough(OnExecuteTask binding) =>
        binding.Mapping is { HasMappingCode: true } ? TaskMappingKind.Script : TaskMappingKind.PassThrough;
}

/// <summary>
/// The <see cref="ITaskMappingRunner"/> executors use: routes each phase through <see cref="TaskMappingRouter"/> to the
/// <see cref="ScriptTaskMappingRunner"/> or the <see cref="DeclarativeTaskMappingRunner"/>, and passes through when the
/// binding maps nothing in that phase.
/// </summary>
public sealed class RoutingTaskMappingRunner(
    ScriptTaskMappingRunner scriptRunner,
    DeclarativeTaskMappingRunner declarativeRunner) : ITaskMappingRunner
{
    /// <summary>
    /// A runner over <paramref name="scriptEngine"/> and the process-wide expression compiler, for executors built by
    /// hand (tests, tools) without the DI-registered runner. Behaves like the registered one.
    /// </summary>
    public static ITaskMappingRunner CreateDefault(IScriptEngine scriptEngine) =>
        new RoutingTaskMappingRunner(
            new ScriptTaskMappingRunner(scriptEngine),
            new DeclarativeTaskMappingRunner(ExpressionCompiler.Shared, TimeProvider.System));

    /// <inheritdoc />
    public Task<Result<ScriptResponse?>> PrepareInputAsync(
        WorkflowTask task, TaskExecutorContext ctx, CancellationToken ct, TaskMappingSite? site = null) =>
        TaskMappingRouter.InputKind(ctx.OnExecuteTask) switch
        {
            TaskMappingKind.Declarative => declarativeRunner.PrepareInputAsync(task, ctx, ct, site),
            TaskMappingKind.Script => scriptRunner.PrepareInputAsync(task, ctx, ct, site),
            _ => Task.FromResult(Result<ScriptResponse?>.Ok(null))
        };

    /// <inheritdoc />
    public Task<Result<object?>> ProcessOutputAsync(
        WorkflowTask task, TaskInvocationResult result, TaskExecutorContext ctx, CancellationToken ct,
        TaskMappingSite? site = null) =>
        TaskMappingRouter.OutputKind(ctx.OnExecuteTask) switch
        {
            TaskMappingKind.Declarative => declarativeRunner.ProcessOutputAsync(task, result, ctx, ct, site),
            TaskMappingKind.Script => scriptRunner.ProcessOutputAsync(task, result, ctx, ct, site),
            _ => Task.FromResult(Result<object?>.Ok(result.Data))
        };
}
