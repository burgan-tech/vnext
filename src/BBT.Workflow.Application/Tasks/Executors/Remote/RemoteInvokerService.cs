using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Aether.Tracing;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Implementation of IRemoteInvokerService using Dapr service invocation.
/// Handles the communication with the Execution Service for remote task execution.
/// <para>
/// The HTTP transport goes through <see cref="DaprServiceInvocationClient"/> —
/// <c>DaprClient.CreateInvokeHttpClient()</c> under the hood — because the SDK's
/// <c>DaprClient.InvokeMethod*</c> family is <c>[Obsolete]</c> in 1.17. Same wire (HTTP to the
/// local sidecar), same headers, same result mapping.
/// </para>
/// <para>
/// Retry layering: NO retry runs at the transport layer. The Dapr sidecar
/// (<c>etc/orchestration/dapr/components/resiliency.yaml</c>) contributes only a circuit
/// breaker (fast-fail when Execution is down) — no retry policy, because Dapr cannot tell an
/// app error response (the task ran, possible side effects) from an unreachable-app failure
/// by status code, and retrying the former would re-invoke a side-effecting task. So this
/// class performs no retries either; a transport failure becomes a task failure that the
/// user-defined error boundary decides on. The linked CTS below owns the per-call budget
/// inside the hierarchy: invocation timeout (60s) ⊂ job budget (300s) ⊂ chain lock lease
/// (330s) — validated at startup by <c>WorkflowExecutionOptionsValidator</c>.
/// </para>
/// </summary>
public sealed class RemoteInvokerService : IRemoteInvokerService
{
    private readonly DaprServiceInvocationClient _daprInvocation;
    private readonly string _executionServiceAppId;
    private readonly int _invocationTimeoutSeconds;
    private readonly ILogger<RemoteInvokerService> _logger;
    private readonly ICorrelationIdProvider _correlationIdProvider;
    /// <summary>Matches the JSON options the SDK's obsolete typed invoke used (Web defaults).</summary>
    private static readonly JsonSerializerOptions InvokeJsonOptions = new(JsonSerializerDefaults.Web);

    public RemoteInvokerService(
        DaprServiceInvocationClient daprInvocation,
        IConfiguration configuration,
        ILogger<RemoteInvokerService> logger,
        ICorrelationIdProvider correlationIdProvider)
    {
        _daprInvocation = daprInvocation;
        _executionServiceAppId = VNextAppIds.ExecutionOrDefault(
            configuration[VNextAppIds.ConfigKeys.Execution],
            configuration[VNextAppIds.ConfigKeys.AppDomain]);
        _invocationTimeoutSeconds = int.TryParse(
            configuration["ExecutionApi:InvocationTimeoutSeconds"], out var t) ? t : 60;
        _logger = logger;
        _correlationIdProvider = correlationIdProvider;
    }

    /// <inheritdoc />
    public async Task<Result<TaskInvocationResult>> InvokeAsync(
        string taskType,
        string taskKey,
        TaskEnvelope envelope,
        TaskTraceContext traceContext,
        CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

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
            var httpRequest = DaprServiceInvocationClient.CreateRequest(
                HttpMethod.Post,
                _executionServiceAppId,
                $"/api/v1/execution/invoke/{taskType}/{taskKey}");
            httpRequest.Content = JsonContent.Create(request);

            httpRequest.Headers.Add(WorkflowInfo.Name, WorkflowInfo.Generate(
                traceContext.Domain ?? "unknown",
                traceContext.WorkflowKey ?? "unknown",
                traceContext.WorkflowVersion ?? "latest",
                traceContext.InstanceId));

            if (traceContext.InstanceId != Guid.Empty)
            {
                httpRequest.Headers.Remove(TelemetryConstants.HeaderNames.WorkflowInstanceId);
                httpRequest.Headers.TryAddWithoutValidation(
                    TelemetryConstants.HeaderNames.WorkflowInstanceId,
                    traceContext.InstanceId.ToString("D").ToLowerInvariant());
            }

            if (Guid.TryParseExact(traceContext.CorrelationId, "N", out var correlationId)
                && correlationId != Guid.Empty)
            {
                httpRequest.Headers.Remove(TelemetryConstants.HeaderNames.CorrelationId);
                httpRequest.Headers.TryAddWithoutValidation(
                    TelemetryConstants.HeaderNames.CorrelationId,
                    correlationId.ToString("N"));
            }

            if (TelemetryConstants.TryNormalizeIdentityClaim(traceContext.Sub, out var subject))
            {
                httpRequest.Headers.Remove(TelemetryConstants.HeaderNames.Sub);
                httpRequest.Headers.TryAddWithoutValidation(
                    TelemetryConstants.HeaderNames.Sub,
                    subject);
            }

            if (TelemetryConstants.TryNormalizeIdentityClaim(traceContext.ActSub, out var actSub))
            {
                httpRequest.Headers.Remove(TelemetryConstants.HeaderNames.ActSub);
                httpRequest.Headers.TryAddWithoutValidation(
                    TelemetryConstants.HeaderNames.ActSub,
                    actSub);
            }

            // Forward root instance ID from Activity baggage (set by TransitionExecutor for subflow instances)
            var rootIdBaggage = Activity.Current?.GetBaggageItem(TelemetryConstants.TagNames.RootInstanceId);
            if (!string.IsNullOrEmpty(rootIdBaggage))
                httpRequest.Headers.TryAddWithoutValidation(TelemetryConstants.HeaderNames.RootInstanceId, rootIdBaggage);

            // Forward the originating request id so Execution's correlation middleware and
            // log enrichers pick it up — this is what joins Execution logs to the client request.
            // Distinct from CorrelationId above: RequestId is per client request, CorrelationId
            // is the chain-stable business correlation.
            if (!string.IsNullOrEmpty(traceContext.RequestId))
                httpRequest.Headers.TryAddWithoutValidation(TelemetryConstants.HeaderNames.RequestId, traceContext.RequestId);

            // Same contract the obsolete InvokeMethodAsync<T> had: a non-2xx is a failed invoke (it
            // threw InvocationException; EnsureSuccessStatusCode throws HttpRequestException — both
            // land in the catch-all below as a 500 failure result), and the body is deserialized
            // with the SDK's default Web options.
            using var httpResponse = await _daprInvocation.SendAsync(httpRequest, invocationCts.Token);
            httpResponse.EnsureSuccessStatusCode();
            var response = await httpResponse.Content.ReadFromJsonAsync<TaskInvokeResponse>(
                               InvokeJsonOptions, invocationCts.Token)
                           ?? throw new InvalidOperationException("Execution returned an empty invoke response body");

            var remoteResult = new TaskInvocationResult
            {
                IsSuccess = response.Result!.IsSuccess,
                StatusCode = response.Result.StatusCode,
                Body = response.Result.Body,
                Data = response.Result.Data,
                ErrorMessage = response.Result.ErrorMessage,
                Headers = InvocationKeyNormalizer.Normalize(response.Result.Headers),
                TaskType = response.Result.TaskType,
                Metadata = InvocationKeyNormalizer.Normalize(response.Result.Metadata),
                ExecutionDurationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
            };

            return Result<TaskInvocationResult>.Ok(remoteResult);
        }
        catch (OperationCanceledException) when (
            invocationCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Own per-invocation timeout — not caused by parent pipeline cancellation
            _logger.LogError(
                "Dapr invocation timeout after {Seconds}s. TaskType: {TaskType}, TaskKey: {TaskKey} [timeout.layer=remote]",
                _invocationTimeoutSeconds, taskType, taskKey);

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: $"Dapr invocation timeout after {_invocationTimeoutSeconds}s",
                statusCode: 408,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType));
        }
        catch (OperationCanceledException)
        {
            // Parent pipeline cancelled — propagate so the pipeline handles it correctly
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to invoke remote task {TaskKey}: {Error}",
                taskKey, ex.Message);

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: ex.Message,
                statusCode: 500,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
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

        var headers = scriptContext.Headers != null
            ? scriptContext.GetHeadersAsDictionary()
            : null;

        string? requestId = _correlationIdProvider.Get();
        if (string.IsNullOrEmpty(requestId) && headers is not null)
            headers.TryGetValue("x-request-id", out requestId);

        var activity = Activity.Current;

        var subject = GetIdentityClaim(headers, TelemetryConstants.HeaderNames.Sub);
        var actSub = GetIdentityClaim(headers, TelemetryConstants.HeaderNames.ActSub);

        // Business correlation: the chain-stable execution correlation id, published as baggage
        // by TransitionExecutor.EnrichTelemetry for every pipeline run (sync and async alike).
        // Fallback to the current trace id (32 hex, Guid "N"-compatible) so a correlation is
        // always available even when no pipeline baggage exists.
        var correlationId = activity?.GetBaggageItem(TelemetryConstants.TagNames.CorrelationId)
                            ?? activity?.TraceId.ToString();

        return TaskTraceContext.Create(
            instanceId: instance?.Id ?? Guid.Empty,
            domain: domain,
            workflowKey: workflow?.Key ?? string.Empty,
            workflowVersion: workflow?.Version ?? string.Empty,
            correlationId: correlationId,
            traceParent: activity?.Id,
            traceState: activity?.TraceStateString,
            sub: subject,
            actSub: actSub,
            requestId: requestId);
    }

    private static string? GetIdentityClaim(
        IReadOnlyDictionary<string, string>? headers,
        string headerName)
    {
        var rawValue = headers?
            .FirstOrDefault(header => string.Equals(
                header.Key,
                headerName,
                StringComparison.OrdinalIgnoreCase))
            .Value;
        return TelemetryConstants.TryNormalizeIdentityClaim(rawValue, out var normalized)
            ? normalized
            : null;
    }
}
