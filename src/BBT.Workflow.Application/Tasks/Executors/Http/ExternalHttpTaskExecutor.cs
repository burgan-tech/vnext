using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for local HTTP tasks (<see cref="TaskType.ExternalHttp"/>, discriminator "22"):
/// the user-defined URL is invoked directly by the Orchestrator process instead of being
/// routed through the Execution service's <c>/execution/invoke/{type}/{key}</c> hop.
/// <para>
/// The lifecycle is identical to <see cref="HttpTaskExecutor"/> — input mapping runs locally (base template),
/// the task is flattened through the same <see cref="TaskBindingMapper"/> into the same
/// <see cref="HttpTaskBinding"/> the remote path would put on the wire, and output mapping runs
/// locally against the same result shape. Only the transport differs: the binding is handed to
/// <see cref="IExternalHttpTaskInvoker"/> in-process, so no Dapr sidecar, circuit breaker or
/// remote-invocation timeout participates — the task's own <c>timeoutSeconds</c> (default 30)
/// is the only bound below the job budget.
/// </para>
/// <typeparamref name="TTask"/> is <see cref="HttpTask"/> because <see cref="ExternalHttpTask"/>
/// derives from it; the registry routes by <see cref="WorkflowTask.GetTaskType"/>, so plain
/// type-6 HTTP tasks never reach this executor.
/// </summary>
public sealed class ExternalHttpTaskExecutor : TaskExecutorBase<HttpTask>
{
    private readonly IExternalHttpTaskInvoker _localInvoker;
    private readonly IRemoteInvokerService _remoteInvoker;

    /// <summary>
    /// Initializes a new instance of ExternalHttpTaskExecutor.
    /// </summary>
    /// <param name="mappingRunner">
    /// The binding mapping runner (DI); null runs mappings through <see cref="RoutingTaskMappingRunner.CreateDefault"/>
    /// over the script engine.
    /// </param>
    public ExternalHttpTaskExecutor(
        IExternalHttpTaskInvoker localInvoker,
        IScriptEngine scriptEngine,
        IRemoteInvokerService remoteInvoker,
        ILogger<ExternalHttpTaskExecutor> logger,
        ITaskMappingRunner? mappingRunner = null)
        : base(logger, mappingRunner ?? RoutingTaskMappingRunner.CreateDefault(scriptEngine))
    {
        _localInvoker = localInvoker;
        _remoteInvoker = remoteInvoker;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.ExternalHttp;

    /// <inheritdoc />
    protected override string InputMappingFailurePrefix => "External HTTP task input handler failed: ";

    /// <inheritdoc />
    protected override string OutputMappingFailurePrefix => "External HTTP task output handler failed: ";

    /// <inheritdoc />
    /// <remarks>
    /// Kept from the former mapping block, which compiled through the engine's <c>CompileToInstanceAsync</c> once per
    /// phase instead of the per-execution memo. Switching it to the memo would drop the second phase's engine call
    /// (and its compile span); that is not proven neutral for this type, so the legacy compile path is preserved.
    /// </remarks>
    protected override bool MemoizesScriptMappingCompile => false;

    /// <inheritdoc />
    protected override bool FilesResponseWithoutOutputMapping => true;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        HttpTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Flatten through the same binding mapper as the remote path so both HTTP task types share
        // one contract (rawBody precedence, content-type resolution, header serialization).
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

        var binding = envelopeResult.Value!.Binding.Deserialize<HttpTaskBinding>();
        if (binding is null)
        {
            return Result<TaskInvocationResult>.Fail(Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"External HTTP task {task.Key} produced an empty HTTP binding."));
        }

        // The same pipeline-built correlation context the type-6 path puts in its invoke envelope.
        // Passed explicitly because ambient Activity baggage does not survive the task-span
        // subtree in-process (spans created from ActivityContext sever the baggage parent chain).
        var traceContext = _remoteInvoker.CreateTraceContext(context.ScriptContext);

        var result = await _localInvoker.InvokeAsync(task.Key, binding, cancellationToken, traceContext);

        if (!result.IsSuccess && result.StatusCode is null)
        {
            // Transport-level failure (no response). HTTP error responses flow through as results,
            // mirroring the remote path where the error boundary decides.
            Logger.TaskInvocationFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.ErrorMessage ?? "Unknown error");
        }

        return Result<TaskInvocationResult>.Ok(result);
    }
}
