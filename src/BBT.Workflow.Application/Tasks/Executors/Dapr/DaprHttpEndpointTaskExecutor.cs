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
/// Executor for Dapr HTTP endpoint tasks.
/// Handles input mapping locally, then delegates to RemoteInvokerService for execution.
/// </summary>
public sealed class DaprHttpEndpointTaskExecutor : TaskExecutorBase<DaprHttpEndpointTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;

    /// <summary>
    /// Initializes a new instance of DaprHttpEndpointTaskExecutor.
    /// </summary>
    /// <param name="mappingRunner">
    /// The binding mapping runner (DI); null runs mappings through <see cref="RoutingTaskMappingRunner.CreateDefault"/>
    /// over the script engine.
    /// </param>
    public DaprHttpEndpointTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        ILogger<DaprHttpEndpointTaskExecutor> logger,
        ITaskMappingRunner? mappingRunner = null)
        : base(logger, mappingRunner ?? RoutingTaskMappingRunner.CreateDefault(scriptEngine))
    {
        _remoteInvoker = remoteInvoker;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.DaprHttpEndpoint;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        DaprHttpEndpointTask task,
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

        // The caller's credential travels as the request's headers (the task itself declares none).
        var envelope = WithCallerCredential(envelopeResult.Value!, context.ScriptContext);

        var result = await _remoteInvoker.InvokeAsync(
            TaskTypes.DaprHttpEndpoint,
            task.Key,
            envelope,
            traceContext,
            cancellationToken);

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
