using System.Text;
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
/// Executor for HTTP tasks.
/// Input and output mapping run in the base template (<see cref="ITaskMappingRunner"/>); the executor dispatches
/// through <see cref="ITaskInvocationDispatcher"/>, which runs the prepared binding locally or via the Execution service per
/// <see cref="BBT.Workflow.Tasks.Invocation.ITaskInvocationRouter"/>.
/// </summary>
public sealed class HttpTaskExecutor : TaskExecutorBase<HttpTask>
{
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly ITaskInvocationDispatcher _dispatcher;

    /// <summary>
    /// Initializes a new instance of HttpTaskExecutor.
    /// </summary>
    /// <param name="mappingRunner">
    /// The binding mapping runner (DI). Null — an executor built by hand — runs mappings through
    /// <see cref="RoutingTaskMappingRunner.CreateDefault"/> over <paramref name="scriptEngine"/>.
    /// </param>
    public HttpTaskExecutor(
        IRemoteInvokerService remoteInvoker,
        IScriptEngine scriptEngine,
        ITaskInvocationDispatcher dispatcher,
        ILogger<HttpTaskExecutor> logger,
        ITaskMappingRunner? mappingRunner = null)
        : base(logger, mappingRunner ?? RoutingTaskMappingRunner.CreateDefault(scriptEngine))
    {
        _remoteInvoker = remoteInvoker;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Http;

    /// <inheritdoc />
    /// <remarks>An HTTP task files its response on the script context whether or not its binding maps the output.</remarks>
    protected override bool FilesResponseWithoutOutputMapping => true;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        HttpTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // Map the task to its binding object; it is serialized only if the call leaves the process.
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
            (HttpTaskBinding)envelopeResult.Value.Binding, CallerHeadersOf(context.ScriptContext));

        var result = await _dispatcher.DispatchAsync(
            task, Execution.TaskTypes.Http, binding, traceContext, cancellationToken);

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
