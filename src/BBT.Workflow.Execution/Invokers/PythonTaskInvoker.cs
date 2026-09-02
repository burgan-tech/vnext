using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Metrics;
using BBT.Workflow.Execution.Python;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Invokers;

public sealed class PythonTaskInvoker(
    IPythonRuntimeRegistry runtimeRegistry,
    IOptions<PythonOptions> options,
    ILogger<PythonTaskInvoker> logger,
    ITaskMetrics? metrics = null) : ITaskInvoker<PythonTaskBinding>
{
    private readonly PythonOptions _options = options.Value;
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    public string TaskType => TaskTypes.Python;
    public Type BindingType => typeof(PythonTaskBinding);

    public Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<PythonTaskBinding> descriptor,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(descriptor.TaskKey, descriptor.Binding, cancellationToken);

    public Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<PythonTaskBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize PythonTaskBinding");
        return ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        PythonTaskBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var mode = string.IsNullOrWhiteSpace(binding.ExecutionMode)
            ? _options.DefaultMode
            : binding.ExecutionMode;
        using var activity = PythonTaskTelemetry.ActivitySource.StartActivity("python.execute");
        activity?.SetTag("task.type", TaskTypes.Python);
        activity?.SetTag("task.key", taskKey);
        activity?.SetTag("python.execution_mode", mode);
        activity?.SetTag("python.runtime_version", "unknown");

        var validationError = Validate(binding, mode);
        if (validationError is not null)
        {
            return Failure(validationError, "validation_error", mode, stopwatch, 400, activity: activity);
        }

        var inputJson = binding.Input?.GetRawText() ?? "null";
        try
        {
            var runtime = runtimeRegistry.GetRequiredRuntime(mode);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(binding.TimeoutSeconds));

            var result = await runtime.ExecuteAsync(
                new PythonExecutionRequest(
                    binding.Script,
                    binding.Location,
                    inputJson,
                    TimeSpan.FromSeconds(binding.TimeoutSeconds)),
                timeout.Token);

            stopwatch.Stop();
            _metrics.RecordPythonInvocation(mode, "success");
            PythonTaskTelemetry.Record(
                mode,
                "success",
                stopwatch.Elapsed.TotalMilliseconds,
                result.RuntimeVersion);
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag("status", "success");
            activity?.SetTag("python.runtime_version", result.RuntimeVersion);

            logger.LogDebug(
                "Python task {TaskKey} completed in {Mode}; stdout={StdoutBytes} bytes stderr={StderrBytes} bytes",
                taskKey,
                mode,
                Encoding.UTF8.GetByteCount(result.Stdout),
                Encoding.UTF8.GetByteCount(result.Stderr));

            return TaskInvocationResult.Success(
                data: InvokerHelpers.TryParseJson(result.OutputJson),
                body: result.OutputJson,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["ExecutionMode"] = mode,
                    ["RuntimeVersion"] = result.RuntimeVersion,
                    ["StdoutBytes"] = Encoding.UTF8.GetByteCount(result.Stdout),
                    ["StderrBytes"] = Encoding.UTF8.GetByteCount(result.Stderr),
                    ["StdoutTruncated"] = result.StdoutTruncated,
                    ["StderrTruncated"] = result.StderrTruncated
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "timeout");
            return Failure(
                $"Python execution exceeded the {binding.TimeoutSeconds}-second timeout.",
                "timeout",
                mode,
                stopwatch,
                408,
                activity: activity);
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            return Failure(
                "Python execution was cancelled.",
                "cancelled",
                mode,
                stopwatch,
                499,
                activity: activity);
        }
        catch (PythonExecutionException ex)
        {
            logger.LogWarning(
                ex,
                "Python task {TaskKey} failed in {Mode} with reason {Reason}",
                taskKey,
                mode,
                ex.Reason);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Reason);
            activity?.SetTag("error.type", ex.PythonExceptionType ?? ex.GetType().Name);
            activity?.SetTag("python.runtime_version", ex.RuntimeVersion ?? "unknown");
            var statusCode = ex.Reason is "runtime_disabled" or "runtime_unavailable" ? 503 : 422;
            return Failure(
                ex.Message,
                ex.Reason,
                mode,
                stopwatch,
                statusCode,
                ex.PythonExceptionType,
                ex.RuntimeVersion,
                activity);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected Python task failure for {TaskKey} in {Mode}", taskKey, mode);
            activity?.SetStatus(ActivityStatusCode.Error, "unexpected_error");
            return Failure(
                ex.Message,
                "unexpected_error",
                mode,
                stopwatch,
                500,
                ex.GetType().Name,
                activity: activity);
        }
    }

    private string? Validate(PythonTaskBinding binding, string mode)
    {
        if (string.IsNullOrWhiteSpace(binding.Script))
        {
            return "Python task requires a non-empty script.";
        }

        if (Encoding.UTF8.GetByteCount(binding.Script) > _options.MaxCodeBytes)
        {
            return "Python script exceeds the configured size limit.";
        }

        if (!PythonRuntimeModes.All.Contains(mode))
        {
            return "Python executionMode must be one of: pythonNet, process, container.";
        }

        if (binding.Location.Contains('/') || binding.Location.Contains('\\'))
        {
            return "Python script location must be a diagnostic file name, not a filesystem path.";
        }

        if (binding.Input is { } input &&
            Encoding.UTF8.GetByteCount(input.GetRawText()) > _options.MaxInputBytes)
        {
            return "Python input exceeds the configured size limit.";
        }

        if (binding.TimeoutSeconds is < 1 || binding.TimeoutSeconds > _options.MaxTimeoutSeconds)
        {
            return $"Python timeoutSeconds must be between 1 and {_options.MaxTimeoutSeconds}.";
        }

        return null;
    }

    private TaskInvocationResult Failure(
        string message,
        string reason,
        string mode,
        Stopwatch stopwatch,
        int statusCode,
        string? exceptionType = null,
        string? runtimeVersion = null,
        Activity? activity = null)
    {
        stopwatch.Stop();
        _metrics.RecordPythonInvocation(mode, reason);
        PythonTaskTelemetry.Record(mode, reason, stopwatch.Elapsed.TotalMilliseconds, runtimeVersion);
        activity?.SetStatus(ActivityStatusCode.Error, reason);
        activity?.SetTag("status", reason);
        activity?.SetTag("python.runtime_version", runtimeVersion ?? "unknown");

        var metadata = new Dictionary<string, object>
        {
            ["ExecutionMode"] = mode,
            ["Reason"] = reason
        };
        if (!string.IsNullOrWhiteSpace(exceptionType))
        {
            metadata["ExceptionType"] = exceptionType;
        }
        if (!string.IsNullOrWhiteSpace(runtimeVersion))
        {
            metadata["RuntimeVersion"] = runtimeVersion;
        }

        return TaskInvocationResult.Failure(
            error: message,
            statusCode: statusCode,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: metadata);
    }
}
