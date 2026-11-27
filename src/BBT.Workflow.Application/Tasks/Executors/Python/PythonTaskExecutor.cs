using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Invocation.Local;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Calls a handler in the domain's Python runtime container directly from the Orchestration host
/// over Dapr service invocation (<see cref="PythonRuntimeOptions.AppId"/>), with the
/// <see cref="PythonRuntimeContract"/> request/response shape. There is no Execution hop and no
/// Python inside any vNext host: the runtime is a separate, opt-in deployment.
/// Input and output follow the standard task mapping, as for every other task type (base template).
/// </summary>
/// <param name="mappingRunner">The binding mapping runner (DI); null runs mappings over <paramref name="scriptEngine"/>.</param>
public sealed class PythonTaskExecutor(
    IRemoteInvokerService remoteInvoker,
    IScriptEngine scriptEngine,
    DaprServiceInvocationClient daprInvocation,
    IOptions<PythonRuntimeOptions> options,
    ILogger<PythonTaskExecutor> logger,
    ITaskMappingRunner? mappingRunner = null)
    : TaskExecutorBase<PythonTask>(logger, mappingRunner ?? RoutingTaskMappingRunner.CreateDefault(scriptEngine))
{
    private readonly PythonRuntimeOptions _options = options.Value;

    public override TaskType TaskType => TaskType.Python;

    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        PythonTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var instanceId = context.ScriptContext.Instance?.Id ?? Guid.Empty;

        if (string.IsNullOrWhiteSpace(_options.AppId))
        {
            const string message =
                "Python runtime is not enabled for this domain (Workflow:PythonRuntime:AppId is empty).";
            Logger.TaskInvocationFailed(task.Key, TaskType.ToString(), instanceId, message);
            return Result<TaskInvocationResult>.Ok(Failure(message, statusCode: 503));
        }

        // The publish-time check sees only the authored config.input; an input mapping can replace it.
        if (task.Input is { } input && Encoding.UTF8.GetByteCount(input.GetRawText()) > PythonTask.MaxInputBytes)
        {
            var sizeMessage = $"request_too_large: Python task input exceeds the {PythonTask.MaxInputBytes}-byte limit.";
            Logger.TaskInvocationFailed(task.Key, TaskType.ToString(), instanceId, sizeMessage);
            return Result<TaskInvocationResult>.Ok(Failure(sizeMessage, statusCode: 413));
        }

        var trace = remoteInvoker.CreateTraceContext(context.ScriptContext);
        var timeoutSeconds = Math.Clamp(task.TimeoutSeconds, 1, PythonTask.MaxTimeoutSeconds);

        var request = new PythonRuntimeContract.InvokeRequest
        {
            Handler = task.Handler ?? string.Empty,
            Input = task.Input,
            TimeoutMs = timeoutSeconds * 1000,
            Context = new PythonRuntimeContract.InvokeContext
            {
                Domain = trace.Domain,
                Workflow = trace.WorkflowKey,
                WorkflowVersion = trace.WorkflowVersion,
                InstanceId = trace.InstanceId,
                TaskKey = task.Key,
                CorrelationId = trace.CorrelationId
            }
        };

        var binding = new DaprServiceBinding
        {
            AppId = _options.AppId,
            MethodName = _options.InvokeMethod,
            Method = "POST",
            Body = JsonSerializer.Serialize(request)
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + _options.TimeoutGraceSeconds));

        var wire = await DaprServiceInvocation.SendAsync(
            daprInvocation,
            binding,
            TaskTypes.Python,
            timeoutCts.Token,
            LocalInvocationResultMapper.ToWireTraceContext(trace),
            task.Key);

        if (!wire.IsSuccess && HttpTaskInvocation.WasCancelled(wire))
        {
            // Caller cancelled: the pipeline owns this. Only our own timer firing is a task timeout.
            cancellationToken.ThrowIfCancellationRequested();
            var timeoutMessage = $"Python runtime did not answer within {timeoutSeconds + _options.TimeoutGraceSeconds}s.";
            Logger.TaskInvocationFailed(task.Key, TaskType.ToString(), instanceId, timeoutMessage);
            return Result<TaskInvocationResult>.Ok(Failure(timeoutMessage, statusCode: 408));
        }

        var result = LocalInvocationResultMapper.ToOrchestratorResult(wire);
        var (output, errorCode, errorMessage) = PythonRuntimeContract.ReadResponse(wire.Data);

        if (result.IsSuccess)
        {
            // The handler's return value is the task result; the contract envelope stays in Body.
            return Result<TaskInvocationResult>.Ok(Reshape(result, output, result.ErrorMessage));
        }

        var failureMessage = FailureMessage(errorCode, errorMessage, result.ErrorMessage);

        Logger.TaskInvocationFailed(
            task.Key, TaskType.ToString(), instanceId, failureMessage ?? "Unknown error");

        return Result<TaskInvocationResult>.Ok(Reshape(result, result.Data, failureMessage));
    }

    private static string? FailureMessage(string? errorCode, string? errorMessage, string? fallback)
    {
        if (errorMessage is null)
        {
            return fallback;
        }

        return errorCode is null ? errorMessage : $"{errorCode}: {errorMessage}";
    }

    private static TaskInvocationResult Reshape(TaskInvocationResult source, object? data, string? errorMessage) => new()
    {
        IsSuccess = source.IsSuccess,
        StatusCode = source.StatusCode,
        Body = source.Body,
        Data = data,
        ErrorMessage = errorMessage,
        Headers = source.Headers,
        Metadata = source.Metadata,
        TaskType = source.TaskType,
        ExecutionDurationMs = source.ExecutionDurationMs
    };

    private static TaskInvocationResult Failure(string message, int statusCode) => new()
    {
        IsSuccess = false,
        StatusCode = statusCode,
        ErrorMessage = message,
        TaskType = TaskTypes.Python
    };
}
