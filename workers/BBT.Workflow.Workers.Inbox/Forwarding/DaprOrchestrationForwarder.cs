using Dapr.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Workers.Inbox.Forwarding;

/// <summary>
/// <see cref="IOrchestrationForwarder"/> over Dapr service invocation. The orchestration app-id
/// comes from the <c>OrchestrationApi:AppId</c> configuration key — supplied as an ENV variable
/// (e.g. <c>OrchestrationApi__AppId</c>) to the Inbox worker — defaulting to <c>vnext-app</c>.
/// Mirrors <c>RemoteInvokerService</c>: per-invocation timeout via a linked CTS, the
/// <c>X-Workflow</c> header for schema resolution, and rethrow-on-failure for at-least-once retry.
/// </summary>
public sealed class DaprOrchestrationForwarder : IOrchestrationForwarder
{
    private readonly DaprClient _daprClient;
    private readonly string _orchestrationAppId;
    private readonly int _invocationTimeoutSeconds;
    private readonly ILogger<DaprOrchestrationForwarder> _logger;

    public DaprOrchestrationForwarder(
        DaprClient daprClient,
        IConfiguration configuration,
        ILogger<DaprOrchestrationForwarder> logger)
    {
        _daprClient = daprClient;
        _orchestrationAppId = configuration["OrchestrationApi:AppId"] ?? "vnext-app";
        _invocationTimeoutSeconds = int.TryParse(
            configuration["OrchestrationApi:InvocationTimeoutSeconds"], out var t) ? t : 60;
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

        var request = _daprClient.CreateInvokeMethodRequest(method, _orchestrationAppId, route, body);
        request.Headers.Add(
            WorkflowInfo.Name,
            WorkflowInfo.Generate(domain, workflow, version ?? "latest", instanceId));

        try
        {
            await _daprClient.InvokeMethodAsync(request, invocationCts.Token);
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
