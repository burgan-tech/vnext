using System.Text;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for notification tasks.
/// Handles input/output mapping using INotificationScriptProvider for default scripts,
/// and delegates execution to the RemoteInvokerService.
/// 
/// This executor is binding-agnostic - it doesn't know about Dapr bindings.
/// The actual binding resolution happens in NotificationTaskInvoker.
/// </summary>
public sealed class NotificationTaskExecutor : TaskExecutorBase<NotificationTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly IScriptEngine _scriptEngine;
    private readonly INotificationScriptProvider _scriptProvider;

    /// <summary>
    /// Initializes a new instance of NotificationTaskExecutor.
    /// </summary>
    /// <param name="remoteInvoker">The remote invoker service for task execution.</param>
    /// <param name="scriptEngine">The script engine for mapping execution.</param>
    /// <param name="scriptProvider">The notification script provider for default scripts.</param>
    /// <param name="logger">The logger.</param>
    public NotificationTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        INotificationScriptProvider scriptProvider,
        ILogger<NotificationTaskExecutor> logger)
        : base(logger)
    {
        _remoteInvoker = remoteInvoker;
        _scriptEngine = scriptEngine;
        _scriptProvider = scriptProvider;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Notification;

    /// <inheritdoc />
    protected override async Task<Result> PrepareInputAsync(
        NotificationTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    { 
        string? scriptCode = null;
        var defaultScriptResult = await _scriptProvider.GetDefaultScriptAsync(cancellationToken);
        if (defaultScriptResult.IsSuccess)
        {
            scriptCode = defaultScriptResult.Value;
        }
        else
        {
            Logger.NotificationScriptRetrievalFailed(
                task.Key,
                context.ScriptContext.Instance.Id);
        }

        if (string.IsNullOrEmpty(scriptCode))
        {
            return Result.Ok();
        }
        
        var result = await ResultExtensions.TryAsync(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                scriptCode,
                cancellationToken: ct);

            await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Notification task input handler failed: {ex.Message}"));

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
        NotificationTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Create the envelope from the task - Body, Subject, To are already set by mapping
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

        // Send to remote invoker - binding resolution happens in NotificationTaskInvoker
        var result = await _remoteInvoker.InvokeAsync(
            TaskTypes.Notification,
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
        NotificationTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Get the default output script for notification tasks
        string? scriptCode = null;
        var defaultScriptResult = await _scriptProvider.GetDefaultScriptAsync(cancellationToken);
        if (defaultScriptResult.IsSuccess)
        {
            scriptCode = defaultScriptResult.Value;
        }
        else
        {
            Logger.NotificationScriptRetrievalFailed(
                task.Key,
                context.ScriptContext.Instance.Id);
        }

        if (string.IsNullOrEmpty(scriptCode))
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext);

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                scriptCode,
                cancellationToken: ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Notification task output handler failed: {ex.Message}"));

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
