using System.Text;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
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
    private readonly IInstanceQueryGateway _instanceQueryGateway;

    /// <summary>
    /// Initializes a new instance of NotificationTaskExecutor.
    /// </summary>
    public NotificationTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        INotificationScriptProvider scriptProvider,
        IInstanceQueryGateway instanceQueryGateway,
        ILogger<NotificationTaskExecutor> logger)
        : base(logger)
    {
        _remoteInvoker = remoteInvoker;
        _scriptEngine = scriptEngine;
        _scriptProvider = scriptProvider;
        _instanceQueryGateway = instanceQueryGateway;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Notification;

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
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
            return Result<ScriptResponse?>.Ok(null);
        }

        var stateResult = await InjectInstanceStateAsync(context, cancellationToken);
        if (!stateResult.IsSuccess)
        {
            Logger.TaskInputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance.Id,
                stateResult.Error.Message ?? "Failed to resolve instance state");
            return Result<ScriptResponse?>.Fail(stateResult.Error);
        }
        
        var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                scriptCode,
                cancellationToken: ct);

            return await scriptRunner.InputHandler(task, context.ScriptContext);
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

    private async Task<Result> InjectInstanceStateAsync(
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var instance = context.ScriptContext.Instance;
        var instanceIdentifier = !string.IsNullOrWhiteSpace(instance.Key)
            ? instance.Key
            : instance.Id.ToString();

        var input = new GetFunctionWithInstanceInput
        {
            Domain = context.ScriptContext.Workflow.Domain,
            Workflow = context.ScriptContext.Workflow.Key,
            Instance = instanceIdentifier,
            Version = context.ScriptContext.Workflow.Version,
            Extensions = null
        };

        var stateResult = await _instanceQueryGateway.GetFunctionWithStateAsync(input, cancellationToken);
        if (!stateResult.IsSuccess)
        {
            return Result.Fail(Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"Notification task state fetch failed: {stateResult.Error.Message}",
                detail: stateResult.Error.Detail));
        }

        context.ScriptContext.SetBody(new { state = stateResult.Value });
        return Result.Ok();
    }
}
