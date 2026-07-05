using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using Dapr.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Implementation of IRemoteInvokerService using Dapr service invocation.
/// Handles the communication with the Execution Service for remote task execution.
/// <para>
/// Retry layering: connection-level retries and the circuit breaker live in the Dapr
/// sidecar (<c>etc/orchestration/dapr/components/resiliency.yaml</c>) so they apply
/// uniformly to every invocation; this class deliberately performs NO retries of its own —
/// a received response (even 5xx) means the task ran and may already have produced side
/// effects, so the re-run decision belongs to the user-defined error boundary. The linked
/// CTS below owns the per-call budget inside the hierarchy: Dapr retry window ⊂ invocation
/// timeout (60s) ⊂ job budget (300s) ⊂ chain lock lease (330s) — validated at startup by
/// <c>WorkflowExecutionOptionsValidator</c>.
/// </para>
/// </summary>
public sealed class RemoteInvokerService : IRemoteInvokerService
{
    private readonly DaprClient _daprClient;
    private readonly string _executionServiceAppId;
    private readonly int _invocationTimeoutSeconds;
    private readonly ILogger<RemoteInvokerService> _logger;

    public RemoteInvokerService(
        DaprClient daprClient,
        IConfiguration configuration,
        ILogger<RemoteInvokerService> logger)
    {
        _daprClient = daprClient;
        _executionServiceAppId = configuration["ExecutionApi:AppId"] ?? "vnext-execution";
        _invocationTimeoutSeconds = int.TryParse(
            configuration["ExecutionApi:InvocationTimeoutSeconds"], out var t) ? t : 60;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<TaskInvocationResult>> InvokeAsync(
        string taskType,
        string taskKey,
        TaskEnvelope envelope,
        TaskTraceContext traceContext,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        _logger.LogDebug("Invoking remote task {TaskKey} of type {TaskType} on {AppId}",
            taskKey, taskType, _executionServiceAppId);

        var request = new TaskInvokeRequest
        {
            Envelope = envelope,
            TraceContext = traceContext
        };

        // Per-invocation timeout: parent pipeline cancellation takes priority.
        // If only our own timer fires → invocation timeout (timeout.layer=remote).
        using var invocationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        invocationCts.CancelAfter(TimeSpan.FromSeconds(_invocationTimeoutSeconds));

        try
        {
            var httpRequest = _daprClient.CreateInvokeMethodRequest(
                _executionServiceAppId,
                $"/api/v1/execution/invoke/{taskType}/{taskKey}",
                request);

            httpRequest.Headers.Add(WorkflowInfo.Name, WorkflowInfo.Generate(
                traceContext.Domain ?? "unknown",
                traceContext.WorkflowKey ?? "unknown",
                traceContext.WorkflowVersion ?? "latest",
                traceContext.InstanceId));

            // Forward root instance ID from Activity baggage (set by TransitionExecutor for subflow instances)
            var rootIdBaggage = Activity.Current?.GetBaggageItem(TelemetryConstants.TagNames.RootInstanceId);
            if (!string.IsNullOrEmpty(rootIdBaggage))
                httpRequest.Headers.TryAddWithoutValidation(TelemetryConstants.HeaderNames.RootInstanceId, rootIdBaggage);

            var response = await _daprClient.InvokeMethodAsync<TaskInvokeResponse>(
                httpRequest, invocationCts.Token);

            stopwatch.Stop();

            var remoteResult = new TaskInvocationResult
            {
                IsSuccess = response.Result!.IsSuccess,
                StatusCode = response.Result.StatusCode,
                Body = response.Result.Body,
                Data = response.Result.Data,
                ErrorMessage = response.Result.ErrorMessage,
                Headers = response.Result.Headers,
                TaskType = response.Result.TaskType,
                Metadata = response.Result.Metadata,
                ExecutionDurationMs = stopwatch.ElapsedMilliseconds
            };

            return Result<TaskInvocationResult>.Ok(remoteResult);
        }
        catch (OperationCanceledException) when (
            invocationCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Own per-invocation timeout — not caused by parent pipeline cancellation
            stopwatch.Stop();
            _logger.LogError(
                "Dapr invocation timeout after {Seconds}s. TaskType: {TaskType}, TaskKey: {TaskKey} [timeout.layer=remote]",
                _invocationTimeoutSeconds, taskType, taskKey);

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: $"Dapr invocation timeout after {_invocationTimeoutSeconds}s",
                statusCode: 408,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: taskType));
        }
        catch (OperationCanceledException)
        {
            // Parent pipeline cancelled — propagate so the pipeline handles it correctly
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError("Failed to invoke remote task {TaskKey}: {Error}",
                taskKey, ex.Message);

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: ex.Message,
                statusCode: 500,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: taskType));
        }
    }

    /// <inheritdoc />
    public TaskTraceContext CreateTraceContext(ScriptContext scriptContext)
    {
        var instance = scriptContext.Instance;
        var workflow = scriptContext.Workflow;
        var domain = !string.IsNullOrWhiteSpace(workflow?.Domain)
            ? workflow!.Domain
            : scriptContext.Runtime.Domain;

        return TaskTraceContext.Create(
            instanceId: instance?.Id ?? Guid.Empty,
            domain: domain,
            workflowKey: workflow?.Key ?? string.Empty,
            workflowVersion: workflow?.Version ?? string.Empty,
            headers: scriptContext.Headers != null
                ? scriptContext.GetHeadersAsDictionary()
                : null,
            instanceDataJson: instance?.LatestData?.Data?.Json);
    }
}
