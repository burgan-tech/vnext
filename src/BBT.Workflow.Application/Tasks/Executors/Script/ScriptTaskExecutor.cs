using System.Text;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for pure script-based workflow tasks.
/// Compiles and runs custom script code to process workflow data and return results.
/// No remote invocation - all processing happens locally.
/// </summary>
public sealed class ScriptTaskExecutor : TaskExecutorBase<ScriptTask>
{
    private readonly IScriptEngine _scriptEngine;

    /// <summary>
    /// Initializes a new instance of ScriptTaskExecutor.
    /// </summary>
    /// <param name="mappingRunner">
    /// The binding mapping runner (DI); null runs mappings through <see cref="RoutingTaskMappingRunner.CreateDefault"/>
    /// over <paramref name="scriptEngine"/>.
    /// </param>
    public ScriptTaskExecutor(
        IScriptEngine scriptEngine,
        ILogger<ScriptTaskExecutor> logger,
        ITaskMappingRunner? mappingRunner = null)
        : base(logger, mappingRunner ?? RoutingTaskMappingRunner.CreateDefault(scriptEngine))
    {
        _scriptEngine = scriptEngine;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Script;

    /// <inheritdoc />
    protected override string InputMappingFailurePrefix => "Script input handler failed: ";

    /// <inheritdoc />
    /// <remarks>
    /// A script task's script output runs inside <see cref="InvokeAsync"/> (its <c>OutputHandler</c> IS the task), so
    /// this phase passes the invocation's data through, as before. Only a declarative <c>output</c> block (a binding
    /// without a script, so the invocation produced no data) is applied here, over that result.
    /// </remarks>
    protected override Task<Result<object?>> ProcessOutputAsync(
        ScriptTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        return MappingRunner is not null
               && TaskMappingRouter.OutputKind(context.OnExecuteTask) == TaskMappingKind.Declarative
            ? MappingRunner.ProcessOutputAsync(task, invocationResult, context, cancellationToken, MappingSite)
            : Task.FromResult(Result<object?>.Ok(invocationResult.Data));
    }

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        ScriptTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Run output handler to get the result
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: null,
                taskType: TaskType.ToString()));
        }

        var result = await ResultExtensions.TryAsync<TaskInvocationResult>(async ct =>
        {
            var scriptRunner = await GetOrCompileMappingAsync<IMapping>(_scriptEngine, context, ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);

            return TaskInvocationResult.Success(
                data: outputResponse.Data,
                taskType: TaskType.ToString());
        }, cancellationToken, ex => ScriptDiagnostics.TaskScriptError(context.ScriptContext, ex, "Script output handler failed"));

        if (!result.IsSuccess)
        {
            Logger.TaskScriptCompilationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }
        
        // Update script context with response for output handler
        UpdateScriptContextWithResponse(task.Key, result.Value, context.ScriptContext, context.ResponseVariableKey);

        return result;
    }
}

