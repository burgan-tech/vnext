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

    /// <summary>
    /// Initializes a new instance of DaprBindingTaskExecutor.
    /// </summary>
    /// <param name="mappingRunner">
    /// The binding mapping runner (DI); null runs mappings through <see cref="RoutingTaskMappingRunner.CreateDefault"/>
    /// over the script engine.
    /// </param>
    public DaprBindingTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        ITaskInvocationDispatcher dispatcher,
        IScriptEngine scriptEngine,
        ILogger<DaprBindingTaskExecutor> logger,
        ITaskMappingRunner? mappingRunner = null)
        : base(logger, mappingRunner ?? RoutingTaskMappingRunner.CreateDefault(scriptEngine))
    {
        _remoteInvoker = remoteInvoker;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.DaprBinding;

    /// <inheritdoc />
    /// <remarks>
    /// Without an output mapping a binding task contributes no instance data, as before the binding's answer
    /// was returned: an unmapped create/notify task must not start merging the binding's response (an S3
    /// location, an HTTP body) into the instance. Only an output mapping reads the answer.
    /// </remarks>
    protected override object? UnmappedOutput(TaskInvocationResult invocationResult) => null;

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
}
