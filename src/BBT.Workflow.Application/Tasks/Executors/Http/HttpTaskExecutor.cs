using System.Text;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for HTTP tasks.
/// Handles input mapping locally, then delegates to RemoteInvokerService for execution.
/// </summary>
public sealed class HttpTaskExecutor : TaskExecutorBase<HttpTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly IScriptEngine _scriptEngine;

    /// <summary>
    /// Initializes a new instance of HttpTaskExecutor.
    /// </summary>
    public HttpTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        ILogger<HttpTaskExecutor> logger)
        : base(logger)
    {
        _remoteInvoker = remoteInvoker;
        _scriptEngine = scriptEngine;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Http;

    /// <inheritdoc />
    protected override async Task<Result> PrepareInputAsync(
        HttpTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
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
            $"Http task input handler failed: {ex.Message}"));

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
        HttpTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Create envelope for remote execution
        var envelopeResult = TaskBindingMapper.CreateEnvelope(task);
        if (!envelopeResult.IsSuccess)
        {
            Logger.TaskEnvelopeCreationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                envelopeResult.Error.Message ?? "Unknown error");
            return Result<TaskInvocationResult>.Fail(envelopeResult.Error);
        }

        var traceContext = _remoteInvoker.CreateTraceContext(context.ScriptContext);

        var result = await _remoteInvoker.InvokeAsync(
            Execution.TaskTypes.Http,
            task.Key,
            envelopeResult.Value!,
            traceContext,
            cancellationToken);

        if (!result.IsSuccess)
        {
            Logger.TaskInvocationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <inheritdoc />
    protected override async Task<Result<object?>> ProcessOutputAsync(
        HttpTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping == null || string.IsNullOrEmpty(mapping.DecodedCode))
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        // Update script context with response for output handler
        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext);

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping.DecodedCode,
                cancellationToken: ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Http task output handler failed: {ex.Message}"));

        if (!result.IsSuccess)
        {
            Logger.TaskOutputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }
}

