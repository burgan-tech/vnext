using System.Text;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Invocation;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for Dapr binding tasks. Handles input mapping locally, then dispatches the call through
/// <see cref="ITaskInvocationDispatcher"/>: the Execution service (default) or the in-process
/// <c>LocalDaprBindingTaskInvoker</c> (<c>Workflow:TaskInvocation:Modes:daprbinding = Local</c>). Either way the
/// output mapping sees the binding's response (data, metadata), not only that the call happened.
/// </summary>
public sealed class DaprBindingTaskExecutor : TaskExecutorBase<DaprBindingTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly ITaskInvocationDispatcher _dispatcher;
    private readonly IScriptEngine _scriptEngine;

    /// <summary>
    /// Initializes a new instance of DaprBindingTaskExecutor.
    /// </summary>
    public DaprBindingTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        ITaskInvocationDispatcher dispatcher,
        IScriptEngine scriptEngine,
        ILogger<DaprBindingTaskExecutor> logger)
        : base(logger)
    {
        _remoteInvoker = remoteInvoker;
        _dispatcher = dispatcher;
        _scriptEngine = scriptEngine;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.DaprBinding;

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        DaprBindingTask task,
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
            $"DaprBinding task input handler failed: {ScriptDiagnostics.Explain(ex)}"));

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
        DaprBindingTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
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

        var traceContext = _remoteInvoker.CreateTraceContext(context.ScriptContext);

        var result = await _dispatcher.DispatchAsync(
            task, TaskTypes.DaprBinding, envelopeResult.Value!, traceContext, cancellationToken);

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
        DaprBindingTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext, context.ResponseVariableKey);

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await GetOrCompileMappingAsync<IMapping>(_scriptEngine, context, ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"DaprBinding task output handler failed: {ScriptDiagnostics.Explain(ex)}"));

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

