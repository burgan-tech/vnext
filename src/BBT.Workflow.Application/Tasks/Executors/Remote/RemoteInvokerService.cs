using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Aether.Tracing;
using BBT.Workflow.Execution.Grpc;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using Dapr.Client;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Implementation of IRemoteInvokerService using Dapr service invocation.
/// Handles the communication with the Execution Service for remote task execution.
/// <para>
/// Transport: HTTP (default) or gRPC via <c>ExecutionApi:Transport</c> ("http" | "grpc"),
/// both proxied through the Dapr sidecar. Same result contract, same timeout/cancellation
/// semantics, same no-retry policy on both paths — only the wire format differs.
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
    private readonly DaprClient _daprClient;
    private readonly string _executionServiceAppId;
    private readonly int _invocationTimeoutSeconds;
    private readonly ILogger<RemoteInvokerService> _logger;
    private readonly ICorrelationIdProvider _correlationIdProvider;
    private readonly bool _useGrpcTransport;
    private readonly Lazy<TaskInvoker.TaskInvokerClient> _grpcClient;

    public RemoteInvokerService(
        DaprClient daprClient,
        IConfiguration configuration,
        ILogger<RemoteInvokerService> logger,
        ICorrelationIdProvider correlationIdProvider)
    {
        _daprClient = daprClient;
        _executionServiceAppId = configuration["ExecutionApi:AppId"] ?? "vnext-execution";
        _invocationTimeoutSeconds = int.TryParse(
            configuration["ExecutionApi:InvocationTimeoutSeconds"], out var t) ? t : 60;
        _logger = logger;
        _correlationIdProvider = correlationIdProvider;

        _useGrpcTransport = string.Equals(
            configuration["ExecutionApi:Transport"], "grpc", StringComparison.OrdinalIgnoreCase);

        // Lazy on purpose: opening the channel to the Dapr sidecar has a cost (and, on some
        // hosts, a dependency on the sidecar's gRPC port being reachable at all) that
        // environments running the default "http" transport must not pay for or depend on.
        _grpcClient = new Lazy<TaskInvoker.TaskInvokerClient>(() =>
        {
            // Dapr.Client.CreateInvocationInvoker(appId) resolves the sidecar's gRPC endpoint
            // and wires the appId/api-token interceptor for us, but it offers no hook to pass
            // GrpcChannelOptions — and without one, Grpc.Net.Client defaults
            // MaxReceiveMessageSize to 4 MB, well under what a large instance-data payload
            // needs. So the channel is built by hand here, replicating exactly what
            // CreateInvocationInvoker does internally (GrpcChannel.ForAddress +
            // InvocationInterceptor), with our own 64 MB options applied.
            var channel = GrpcChannel.ForAddress(ResolveDaprGrpcEndpoint(), new GrpcChannelOptions
            {
                // Aligned with the sidecar's http-max-request-size: "64" (MB) and the
                // server's AddGrpc limits — the three must agree or large payloads fail
                // on whichever hop has the smallest cap.
                MaxReceiveMessageSize = 64 * 1024 * 1024,
                MaxSendMessageSize = 64 * 1024 * 1024
            });
            var daprApiToken = Environment.GetEnvironmentVariable("DAPR_API_TOKEN") ?? string.Empty;
            var invoker = channel.Intercept(new InvocationInterceptor(_executionServiceAppId, daprApiToken));
            return new TaskInvoker.TaskInvokerClient(invoker);
        });
    }

    /// <summary>
    /// Resolves the Dapr sidecar's gRPC endpoint the same way
    /// <see cref="DaprClient.CreateInvocationInvoker(string, string?, string?)"/> does internally
    /// (<c>DAPR_GRPC_ENDPOINT</c>, else <c>localhost:DAPR_GRPC_PORT</c>, else the sidecar
    /// default port 50001) — duplicated here because that resolution logic lives on an
    /// internal type in Dapr.Common and isn't reachable from this assembly.
    /// </summary>
    private static string ResolveDaprGrpcEndpoint()
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable("DAPR_GRPC_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            var uri = new Uri(configuredEndpoint);
            return new UriBuilder { Scheme = uri.Scheme, Host = uri.Host, Port = uri.Port }.ToString();
        }

        var portValue = Environment.GetEnvironmentVariable("DAPR_GRPC_PORT");
        var port = string.IsNullOrWhiteSpace(portValue) ? 50001 : int.Parse(portValue);
        return new UriBuilder { Scheme = "http", Host = "localhost", Port = port }.ToString();
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

        if (_useGrpcTransport)
            return await InvokeOverGrpcAsync(
                taskType, taskKey, request, traceContext, stopwatch, invocationCts.Token, cancellationToken);

        try
        {
            var httpRequest = _daprClient.CreateInvokeMethodRequest(
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

    /// <summary>
    /// gRPC transport for <see cref="InvokeAsync"/> — same result contract, same timeout and
    /// cancellation semantics as the HTTP path, no transport retry (see class summary).
    /// </summary>
    /// <param name="taskType">The task type being invoked, echoed into the failure result.</param>
    /// <param name="taskKey">The task key being invoked, used for logging only.</param>
    /// <param name="request">The envelope/trace-context request body, serialized as the RPC payload.</param>
    /// <param name="traceContext">Source of the metadata (gRPC's equivalent of HTTP headers) sent with the call.</param>
    /// <param name="stopwatch">Already-started timer shared with the caller for execution-duration reporting.</param>
    /// <param name="invocationToken">
    /// The linked token that carries our own per-invocation timeout on top of
    /// <paramref name="parentToken"/> — the call is made against this one.
    /// </param>
    /// <param name="parentToken">
    /// The caller's own cancellation token, used only to distinguish "parent pipeline
    /// cancelled" (rethrow) from "our own timeout fired" (408 result) in the catch clauses.
    /// </param>
    private async Task<Result<TaskInvocationResult>> InvokeOverGrpcAsync(
        string taskType,
        string taskKey,
        TaskInvokeRequest request,
        TaskTraceContext traceContext,
        Stopwatch stopwatch,
        CancellationToken invocationToken,
        CancellationToken parentToken)
    {
        try
        {
            var metadata = new Metadata
            {
                { WorkflowInfo.Name, WorkflowInfo.Generate(
                    traceContext.Domain ?? "unknown",
                    traceContext.WorkflowKey ?? "unknown",
                    traceContext.WorkflowVersion ?? "latest",
                    traceContext.InstanceId) }
            };

            // Same context keys the HTTP path sends as headers — gRPC metadata ARE HTTP/2
            // headers on the wire, so Execution's header-driven enrichers keep working.
            if (traceContext.InstanceId != Guid.Empty)
                metadata.Add(TelemetryConstants.HeaderNames.WorkflowInstanceId,
                    traceContext.InstanceId.ToString("D").ToLowerInvariant());

            if (Guid.TryParseExact(traceContext.CorrelationId, "N", out var correlationId)
                && correlationId != Guid.Empty)
                metadata.Add(TelemetryConstants.HeaderNames.CorrelationId, correlationId.ToString("N"));

            if (TelemetryConstants.TryNormalizeIdentityClaim(traceContext.Sub, out var subject))
                metadata.Add(TelemetryConstants.HeaderNames.Sub, subject);

            if (TelemetryConstants.TryNormalizeIdentityClaim(traceContext.ActSub, out var actSub))
                metadata.Add(TelemetryConstants.HeaderNames.ActSub, actSub);

            // Forward root instance ID from Activity baggage (set by TransitionExecutor for subflow instances)
            var rootIdBaggage = Activity.Current?.GetBaggageItem(TelemetryConstants.TagNames.RootInstanceId);
            if (!string.IsNullOrEmpty(rootIdBaggage))
                metadata.Add(TelemetryConstants.HeaderNames.RootInstanceId, rootIdBaggage);

            // Forward the originating request id so Execution's correlation middleware and
            // log enrichers pick it up — this is what joins Execution logs to the client request.
            if (!string.IsNullOrEmpty(traceContext.RequestId))
                metadata.Add(TelemetryConstants.HeaderNames.RequestId, traceContext.RequestId);

            var reply = await _grpcClient.Value.InvokeAsync(
                new InvokeRequest
                {
                    TaskType = taskType,
                    TaskKey = taskKey,
                    PayloadJson = TaskInvokePayload.Serialize(request)
                },
                new CallOptions(
                    headers: metadata,
                    deadline: DateTime.UtcNow.AddSeconds(_invocationTimeoutSeconds),
                    cancellationToken: invocationToken));

            stopwatch.Stop();
            var response = TaskInvokePayload.Deserialize<TaskInvokeResponse>(reply.PayloadJson);

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
        catch (RpcException) when (parentToken.IsCancellationRequested)
        {
            // Parent pipeline cancelled — propagate so the pipeline handles it correctly.
            // Grpc.Net.Client surfaces our own token's cancellation as RpcException(Cancelled)
            // rather than OperationCanceledException, so this case must be caught here too —
            // the "which token fired" check is what makes the distinction, not the exception type.
            stopwatch.Stop();
            throw new OperationCanceledException(parentToken);
        }
        catch (RpcException ex)
        {
            stopwatch.Stop();
            _logger.LogError("gRPC invocation of task {TaskKey} failed: {Status} {Detail}",
                taskKey, ex.StatusCode, ex.Status.Detail);

            return Result<TaskInvocationResult>.Ok(
                MapRpcFailure(ex, stopwatch.ElapsedMilliseconds, taskType, _invocationTimeoutSeconds));
        }
        catch (OperationCanceledException) when (!parentToken.IsCancellationRequested)
        {
            // Own per-invocation timeout — not caused by parent pipeline cancellation
            stopwatch.Stop();
            _logger.LogError(
                "gRPC invocation timeout after {Seconds}s. TaskType: {TaskType}, TaskKey: {TaskKey} [timeout.layer=remote]",
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
    }

    /// <summary>
    /// Maps a gRPC transport failure to the same <see cref="TaskInvocationResult"/> shapes the
    /// HTTP path produces for the equivalent failure, so the error boundary sees one contract
    /// regardless of transport: <see cref="StatusCode.DeadlineExceeded"/> → 408 (matches the
    /// HTTP path's own-timeout result), everything else → 500 (matches the HTTP path's generic
    /// transport-failure result). Internal (not public) so
    /// <c>BBT.Workflow.Application.Tests</c> can exercise it directly via
    /// <c>InternalsVisibleTo</c> without a live gRPC call.
    /// </summary>
    internal static TaskInvocationResult MapRpcFailure(
        RpcException ex, long elapsedMs, string taskType, int timeoutSeconds)
        => ex.StatusCode == StatusCode.DeadlineExceeded
            ? TaskInvocationResult.Failure(
                error: $"Dapr invocation timeout after {timeoutSeconds}s",
                statusCode: 408,
                executionDurationMs: elapsedMs,
                taskType: taskType)
            : TaskInvocationResult.Failure(
                error: $"{ex.StatusCode}: {ex.Status.Detail}",
                statusCode: 500,
                executionDurationMs: elapsedMs,
                taskType: taskType);

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
            headers: headers,
            instanceDataJson: instance?.LatestData?.Data?.Json,
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
