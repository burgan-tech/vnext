using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Invocation;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for SOAP tasks.
/// Handles input mapping locally, then dispatches through <see cref="ITaskInvocationDispatcher"/>,
/// which runs the prepared binding locally or via the Execution service per
/// <see cref="BBT.Workflow.Tasks.Invocation.ITaskInvocationRouter"/>.
/// </summary>
public sealed class SoapTaskExecutor : TaskExecutorBase<SoapTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly ITaskInvocationDispatcher _dispatcher;

    /// <param name="mappingRunner">
    /// The binding mapping runner (DI); null runs mappings through <see cref="RoutingTaskMappingRunner.CreateDefault"/>
    /// over the script engine.
    /// </param>
    public SoapTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        ITaskInvocationDispatcher dispatcher,
        ILogger<SoapTaskExecutor> logger,
        ITaskMappingRunner? mappingRunner = null)
        : base(logger, mappingRunner ?? RoutingTaskMappingRunner.CreateDefault(scriptEngine))
    {
        _remoteInvoker = remoteInvoker;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Soap;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        SoapTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var envelopeResult = TaskBindingMapper.CreateBinding(task);
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

        // The caller's credential travels with the task's own headers, in-process and to the Execution host alike.
        // Applied to the mapped binding object: the dispatcher serializes it only when the call leaves the
        // process (Remote), so the default Local path pays no JSON round trip.
        var binding = TaskBindingCredentials.WithCallerCredential(
            (SoapTaskBinding)envelopeResult.Value.Binding, CallerHeadersOf(context.ScriptContext));

        var result = await _dispatcher.DispatchAsync(
            task, Execution.TaskTypes.Soap, binding, traceContext, cancellationToken);

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
}
