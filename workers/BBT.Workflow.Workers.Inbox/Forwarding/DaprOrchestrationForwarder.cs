using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Tracing;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Logging;
using BBT.Workflow.Workers.Inbox.Tracing;
using BBT.Workflow.Shared;
using Dapr.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Workers.Inbox.Forwarding;

/// <summary>
/// <see cref="IOrchestrationForwarder"/> over Dapr service invocation using a Dapr-invokable
/// <see cref="HttpClient"/> (<see cref="DaprClient.CreateInvokeHttpClient(string,string,string)"/>) —
/// Dapr's recommended, non-obsolete invocation API: requests to a relative path are routed through
/// the sidecar to the target app. The orchestration app-id comes from the
/// <c>OrchestrationApi:AppId</c> configuration key, supplied as an ENV variable (e.g.
/// <c>OrchestrationApi__AppId</c>) to the Inbox worker; when unset it falls back to the
/// conventional <c>vnext-{domain}-app</c> via <see cref="VNextAppIds"/>. The old hardcoded
/// <c>vnext-app</c> default was only correct for the <c>core</c> domain.
/// Adds the <c>X-Workflow</c> header for schema resolution.
/// <para>
/// Retries (rethrows) ONLY on transient failures so the inbox processor re-delivers (at-least-once):
/// the orchestration service being unreachable (transport error / invocation timeout) or returning a
/// transient status (5xx, 408, 429). Non-transient error responses (e.g. 4xx) indicate a request that
/// will never succeed on retry — they are logged and ignored so the message is not redelivered forever.
/// </para>
/// </summary>
public sealed class DaprOrchestrationForwarder : IOrchestrationForwarder
{
    private readonly HttpClient _httpClient;
    private readonly string _orchestrationAppId;
    private readonly int _invocationTimeoutSeconds;
    private readonly ILogger<DaprOrchestrationForwarder> _logger;
    private readonly ICorrelationIdProvider? _correlationIdProvider;
    private readonly IRuntimeCallerSigner _runtimeCallerSigner;

    public DaprOrchestrationForwarder(
        IConfiguration configuration,
        ILogger<DaprOrchestrationForwarder> logger,
        ICorrelationIdProvider? correlationIdProvider = null,
        IRuntimeCallerSigner? runtimeCallerSigner = null)
        : this(
            // Invokable client: relative requests are rewritten to the Dapr invoke endpoint for appId.
            DaprClient.CreateInvokeHttpClient(appId: OrchestratorAppId(configuration)),
            OrchestratorAppId(configuration),
            int.TryParse(configuration["OrchestrationApi:InvocationTimeoutSeconds"], out var t) ? t : 60,
            logger,
            correlationIdProvider,
            runtimeCallerSigner)
    {
    }

    /// <summary>Test seam: a forwarder over a given client.</summary>
    internal DaprOrchestrationForwarder(
        HttpClient httpClient,
        string orchestrationAppId,
        int invocationTimeoutSeconds,
        ILogger<DaprOrchestrationForwarder> logger,
        ICorrelationIdProvider? correlationIdProvider = null,
        IRuntimeCallerSigner? runtimeCallerSigner = null)
    {
        _httpClient = httpClient;
        _orchestrationAppId = orchestrationAppId;
        _invocationTimeoutSeconds = invocationTimeoutSeconds;
        _logger = logger;
        _correlationIdProvider = correlationIdProvider;
        _runtimeCallerSigner = runtimeCallerSigner ?? NullRuntimeCallerSigner.Instance;
    }

    private static string OrchestratorAppId(IConfiguration configuration)
        => VNextAppIds.OrchestratorOrDefault(
            configuration[VNextAppIds.ConfigKeys.Orchestrator],
            configuration[VNextAppIds.ConfigKeys.AppDomain]);

    /// <inheritdoc />
    public async Task ForwardAsync<TBody>(
        HttpMethod method,
        string route,
        TBody body,
        string domain,
        string workflow,
        string? version,
        Guid instanceId,
        CancellationToken cancellationToken,
        RuntimeCallTarget? runtimeCallTarget = null)
    {
        // The Inbox worker's ONLY outbound call. Without this span the timeout budget, the
        // transient/non-transient decision and the deliberate drop all collapse into one generic
        // HTTP client span — so a delayed delivery and a permanently dropped one look the same.
        using var activity = EventTraceScope.ActivitySource.StartActivity(
            "Inbox.Forward", ActivityKind.Client);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        activity?.SetTag(TelemetryConstants.TagNames.Domain, domain);
        activity?.SetTag(TelemetryConstants.TagNames.Flow, workflow);
        activity?.SetTag(TelemetryConstants.TagNames.InstanceId, instanceId);

        // Per-invocation timeout; parent cancellation takes priority.
        using var invocationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        invocationCts.CancelAfter(TimeSpan.FromSeconds(_invocationTimeoutSeconds));

        using var request = new HttpRequestMessage(method, route)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add(
            WorkflowInfo.Name,
            WorkflowInfo.Generate(domain, workflow, version ?? "latest", instanceId));

        var rootIdBaggage = Activity.Current?.GetBaggageItem(TelemetryConstants.TagNames.RootInstanceId);
        if (!string.IsNullOrEmpty(rootIdBaggage))
            request.Headers.TryAddWithoutValidation(TelemetryConstants.HeaderNames.RootInstanceId, rootIdBaggage);

        // Forward the originating request id (scoped by EventTraceScope in the handler) so the
        // orchestration side keeps log correlation with the client request that started the flow.
        var correlationId = _correlationIdProvider?.Get();
        if (!string.IsNullOrEmpty(correlationId))
            request.Headers.TryAddWithoutValidation(TelemetryConstants.HeaderNames.RequestId, correlationId);

        // This forward carries no user: on a gated endpoint only the token proves it is the runtime (vnext#1265).
        if (runtimeCallTarget is { } target)
            _runtimeCallerSigner.Stamp(request, target, RuntimeCallerClasses.Inbox);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, invocationCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller / worker shutdown — propagate; not a forward failure to re-deliver.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Per-invocation timeout fired (parent token not cancelled): the service is unreachable
            // or too slow. Transient — rethrow so the inbox processor re-delivers.
            SetOutcome(activity, "timeout");
            _logger.LogError(
                ex,
                "Timed out forwarding {Method} {Route} to {AppId} for instance {InstanceId}; will re-deliver",
                method, route, _orchestrationAppId, instanceId);
            throw;
        }
        catch (HttpRequestException ex)
        {
            // Could not reach the orchestration service (connection refused / DNS / socket).
            // Transient — rethrow so the inbox processor re-delivers (at-least-once); orchestration-side
            // services are idempotent (active-job/terminal-state guards).
            SetOutcome(activity, "unreachable");
            _logger.LogError(
                ex,
                "Failed to reach {AppId} forwarding {Method} {Route} for instance {InstanceId}; will re-deliver",
                _orchestrationAppId, method, route, instanceId);
            throw;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                SetOutcome(activity, "ok");
                _logger.LogDebug(
                    "Forwarded {Method} {Route} to {AppId} for instance {InstanceId}",
                    method, route, _orchestrationAppId, instanceId);
                return;
            }

            var statusCode = (int)response.StatusCode;
            var responseBody = await ReadBodySafelyAsync(response, cancellationToken);

            if (TransientHttpStatus.IsTransient(response.StatusCode))
            {
                // Transient server-side failure — rethrow so the inbox processor re-delivers.
                SetOutcome(activity, "transient", statusCode);
                _logger.LogError(
                    "Transient {StatusCode} forwarding {Method} {Route} to {AppId} for instance {InstanceId}; will re-deliver. Response: {Response}",
                    statusCode, method, route, _orchestrationAppId, instanceId, responseBody);
                throw new HttpRequestException(
                    $"Transient response {statusCode} from '{_orchestrationAppId}' for {method} {route}.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            // A gate refusal of a SIGNED forward is a configuration state (a key ring not yet on this host,
            // rotated, or clocks apart), not a bad request: redeliver it so a rollout gap delays the completion
            // instead of dropping it; the inbox dead-letters it after its retry budget (vnext#1265). An unsigned
            // forward that meets the same code keeps the drop.
            if (runtimeCallTarget is not null && IsSubEndpointGateRefusal(response.StatusCode, responseBody))
            {
                SetOutcome(activity, "gate_refused", statusCode);
                _logger.InboxForwardRefusedByGate(method.Method, route, instanceId);
                throw new HttpRequestException(
                    $"Sub-endpoint gate on '{_orchestrationAppId}' refused {method} {route}.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            // Non-transient error response (e.g. 4xx): the request will never succeed on retry.
            // Log and ignore so the inbox does not re-deliver it forever. This is the outcome most
            // worth seeing: the delivery is gone on purpose and nothing downstream will say so.
            SetOutcome(activity, "non_transient", statusCode);
            _logger.LogError(
                "Non-transient {StatusCode} forwarding {Method} {Route} to {AppId} for instance {InstanceId}; ignoring. Response: {Response}",
                statusCode, method, route, _orchestrationAppId, instanceId, responseBody);
        }
    }

    /// <summary>
    /// Whether an error response is the Orchestration's sub-endpoint gate refusing the call: 403 with
    /// error code <c>Authorization:110001</c>. Consulted only for a signed forward (the <c>complete</c>
    /// backup), where only that gate produces the code.
    /// </summary>
    internal static bool IsSubEndpointGateRefusal(HttpStatusCode statusCode, string responseBody)
    {
        if (statusCode != HttpStatusCode.Forbidden || string.IsNullOrWhiteSpace(responseBody))
            return false;

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.String
                   && code.GetString() == WorkflowErrorCodes.AuthorizationRoleDenied;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stamps how the forward ended. A dropped delivery is not an exception anywhere, so without
    /// this tag it is indistinguishable from a successful one in the trace.
    /// </summary>
    private static void SetOutcome(Activity? activity, string outcome, int? statusCode = null)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.ForwardOutcome, outcome);
        if (statusCode.HasValue)
            activity.SetTag(TelemetryConstants.TagNames.ForwardStatusCode, statusCode.Value);
        if (outcome is not "ok")
            activity.SetStatus(ActivityStatusCode.Error, outcome);
    }

    /// <summary>
    /// Best-effort read of the error response body for diagnostics; never throws.
    /// </summary>
    private async Task<string> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read forward error response body from {AppId}", _orchestrationAppId);
            return "<unavailable>";
        }
    }
}
