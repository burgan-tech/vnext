using System.Text;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Invocation;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for HTTP tasks.
/// Handles input mapping locally, then dispatches through <see cref="ITaskInvocationDispatcher"/>,
/// which runs the prepared binding locally or via the Execution service per
/// <see cref="BBT.Workflow.Tasks.Invocation.ITaskInvocationRouter"/>.
/// </summary>
public sealed class HttpTaskExecutor : TaskExecutorBase<HttpTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly IScriptEngine _scriptEngine;
    private readonly ITaskInvocationDispatcher _dispatcher;

    /// <summary>
    /// Initializes a new instance of HttpTaskExecutor.
    /// </summary>
    public HttpTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        ITaskInvocationDispatcher dispatcher,
        ILogger<HttpTaskExecutor> logger)
        : base(logger)
    {
        _remoteInvoker = remoteInvoker;
        _scriptEngine = scriptEngine;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Http;

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        HttpTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<ScriptResponse?>.Ok(null);
        }

        var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            var scriptRunner = await GetOrCompileMappingAsync<IMapping>(_scriptEngine, context, ct);

            return await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Http task input handler failed: {ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess)
        {
            Logger.TaskInputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
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
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                envelopeResult.Error.Message ?? "Unknown error");
            return Result<TaskInvocationResult>.Fail(envelopeResult.Error);
        }

        // The trace context still comes from the remote invoker service: it is the one place that
        // knows how to derive correlation, identity and request id from a ScriptContext, and both
        // dispatch paths need the same object.
        var traceContext = _remoteInvoker.CreateTraceContext(context.ScriptContext);

        var result = await _dispatcher.DispatchAsync(
            task, Execution.TaskTypes.Http, envelopeResult.Value!, traceContext, cancellationToken);

        if (!result.IsSuccess)
        {
            Logger.TaskInvocationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
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
        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext, context.ResponseVariableKey);

        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await GetOrCompileMappingAsync<IMapping>(_scriptEngine, context, ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Http task output handler failed: {ScriptDiagnostics.Explain(ex)}"));

        if (!result.IsSuccess)
        {
            Logger.TaskOutputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }
}
