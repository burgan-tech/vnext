using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Invocation;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for State Store tasks.
/// Handles input mapping locally, then dispatches through <see cref="ITaskInvocationDispatcher"/>,
/// which runs the prepared binding locally or via the Execution service per
/// <see cref="BBT.Workflow.Tasks.Invocation.ITaskInvocationRouter"/>.
/// </summary>
public sealed class StateStoreTaskExecutor : TaskExecutorBase<StateStoreTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly ITaskInvocationDispatcher _dispatcher;

    /// <summary>
    /// Initializes a new instance of StateStoreTaskExecutor.
    /// </summary>
    /// <param name="mappingRunner">
    /// The binding mapping runner (DI); null runs mappings through <see cref="RoutingTaskMappingRunner.CreateDefault"/>
    /// over the script engine.
    /// </param>
    public StateStoreTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        ITaskInvocationDispatcher dispatcher,
        ILogger<StateStoreTaskExecutor> logger,
        ITaskMappingRunner? mappingRunner = null)
        : base(logger, mappingRunner ?? RoutingTaskMappingRunner.CreateDefault(scriptEngine))
    {
        _remoteInvoker = remoteInvoker;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.StateStore;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        StateStoreTask task,
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

        var traceContext = _remoteInvoker.CreateTraceContext(context.ScriptContext);

        // The mapped binding object goes to the dispatcher as-is; it is serialized only for Remote.
        var result = await _dispatcher.DispatchAsync(
            task, Execution.TaskTypes.StateStore, (StateStoreBinding)envelopeResult.Value.Binding, traceContext, cancellationToken);

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
