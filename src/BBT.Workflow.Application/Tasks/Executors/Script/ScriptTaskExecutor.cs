using System.Text;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
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
    public ScriptTaskExecutor(
        IScriptEngine scriptEngine,
        ILogger<ScriptTaskExecutor> logger)
        : base(logger)
    {
        _scriptEngine = scriptEngine;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Script;

    /// <inheritdoc />
    protected override async Task<Result> PrepareInputAsync(
        ScriptTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Compile and run input handler
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping == null || string.IsNullOrEmpty(mapping.DecodedCode))
        {
            return Result.Ok();
        }

        var result = await ResultExtensions.TryAsync(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping.DecodedCode,
                cancellationToken: ct);

            await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Script input handler failed: {ex.Message}"));

        if (!result.IsSuccess)
        {
            Logger.TaskInputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        ScriptTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Run output handler to get the result
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping == null || string.IsNullOrEmpty(mapping.DecodedCode))
        {
            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: null,
                taskType: TaskType.ToString()));
        }

        var result = await ResultExtensions.TryAsync<TaskInvocationResult>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping.DecodedCode,
                cancellationToken: ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);

            return TaskInvocationResult.Success(
                data: outputResponse.Data,
                taskType: TaskType.ToString());
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Script output handler failed: {ex.Message}"));

        if (!result.IsSuccess)
        {
            Logger.TaskScriptCompilationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                result.Error.Message ?? "Unknown error");
        }
        
        // Update script context with response for output handler
        UpdateScriptContextWithResponse(task.Key, result.Value, context.ScriptContext);

        return result;
    }
}

