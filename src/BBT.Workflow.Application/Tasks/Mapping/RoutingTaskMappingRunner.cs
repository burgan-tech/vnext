using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Tasks.Executors;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Picks the runner for one phase of a binding, the same way <c>RoutingConditionEvaluator</c> picks a condition
/// evaluator: the Roslyn script when <see cref="OnExecuteTask.Mapping"/> has code, else the declarative block of that
/// phase when the binding authors one, else pass-through (input → null, output → <c>result.Data</c>, exactly what an
/// executor did without a mapping). Publish rejects a binding that carries both a script and blocks (D-4); if one reaches
/// the runtime anyway (data stored before that validation existed), the script wins — its legacy meaning — and the
/// blocks are ignored.
/// </summary>
public static class TaskMappingRouter
{
    /// <summary>The kind that handles <paramref name="binding"/>'s input phase.</summary>
    public static TaskMappingKind InputKind(OnExecuteTask binding) => KindOf(binding, binding.Input);

    /// <summary>The kind that handles <paramref name="binding"/>'s output phase.</summary>
    public static TaskMappingKind OutputKind(OnExecuteTask binding) => KindOf(binding, binding.Output);

    private static TaskMappingKind KindOf(OnExecuteTask binding, MappingBlock? block)
    {
        if (HasScript(binding))
            return TaskMappingKind.Script;
        return block is not null ? TaskMappingKind.Declarative : TaskMappingKind.PassThrough;
    }

    private static bool HasScript(OnExecuteTask binding) => binding.Mapping is { HasMappingCode: true };
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
