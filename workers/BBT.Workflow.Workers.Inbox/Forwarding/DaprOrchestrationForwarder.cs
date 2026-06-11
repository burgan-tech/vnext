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
/// Adds the <c>X-Workflow</c> header for schema resolution and rethrows on failure so the inbox
/// processor re-delivers (at-least-once).
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

        try
        {
            using var response = await _httpClient.SendAsync(request, invocationCts.Token);
            response.EnsureSuccessStatusCode();
            _logger.LogDebug(
                "Forwarded {Method} {Route} to {AppId} for instance {InstanceId}",
                method, route, _orchestrationAppId, instanceId);
        }
        catch (Exception ex)
        {
            // Rethrow so the inbox processor re-delivers (at-least-once); orchestration-side
            // services are idempotent (active-job/terminal-state guards).
            _logger.LogError(
                ex,
                "Failed to forward {Method} {Route} to {AppId} for instance {InstanceId}",
                method, route, _orchestrationAppId, instanceId);
            throw;
        }
    }
}
