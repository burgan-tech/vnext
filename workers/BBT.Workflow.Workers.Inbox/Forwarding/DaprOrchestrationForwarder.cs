using System.Net;
using System.Net.Http.Json;
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
/// <c>OrchestrationApi__AppId</c>) to the Inbox worker, defaulting to <c>vnext-app</c>.
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

    public DaprOrchestrationForwarder(
        IConfiguration configuration,
        ILogger<DaprOrchestrationForwarder> logger)
    {
        _orchestrationAppId = configuration["OrchestrationApi:AppId"] ?? "vnext-app";
        _invocationTimeoutSeconds = int.TryParse(
            configuration["OrchestrationApi:InvocationTimeoutSeconds"], out var t) ? t : 60;
        // Invokable client: relative requests are rewritten to the Dapr invoke endpoint for appId.
        _httpClient = DaprClient.CreateInvokeHttpClient(appId: _orchestrationAppId);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ForwardAsync<TBody>(
        HttpMethod method,
        string route,
        TBody body,
        string domain,
        string workflow,
        string? version,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
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
                _logger.LogDebug(
                    "Forwarded {Method} {Route} to {AppId} for instance {InstanceId}",
                    method, route, _orchestrationAppId, instanceId);
                return;
            }

            var statusCode = (int)response.StatusCode;
            var responseBody = await ReadBodySafelyAsync(response, cancellationToken);

            if (IsTransientStatus(response.StatusCode))
            {
                // Transient server-side failure — rethrow so the inbox processor re-delivers.
                _logger.LogError(
                    "Transient {StatusCode} forwarding {Method} {Route} to {AppId} for instance {InstanceId}; will re-deliver. Response: {Response}",
                    statusCode, method, route, _orchestrationAppId, instanceId, responseBody);
                throw new HttpRequestException(
                    $"Transient response {statusCode} from '{_orchestrationAppId}' for {method} {route}.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            // Non-transient error response (e.g. 4xx): the request will never succeed on retry.
            // Log and ignore so the inbox does not re-deliver it forever.
            _logger.LogError(
                "Non-transient {StatusCode} forwarding {Method} {Route} to {AppId} for instance {InstanceId}; ignoring. Response: {Response}",
                statusCode, method, route, _orchestrationAppId, instanceId, responseBody);
        }
    }

    /// <summary>
    /// Transient HTTP statuses worth retrying: any 5xx (server error / unavailable), 408 Request
    /// Timeout, and 429 Too Many Requests.
    /// </summary>
    private static bool IsTransientStatus(HttpStatusCode status) =>
        (int)status > 500
        || status == HttpStatusCode.RequestTimeout
        || status == HttpStatusCode.TooManyRequests;

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
