using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution.RuntimeCaller;

namespace BBT.Workflow.Workers.Inbox.Forwarding;

/// <summary>
/// Forwards an inbox-received event to an Orchestration internal endpoint via Dapr service
/// invocation. The Inbox worker is a thin relay: it performs no domain processing and owns no
/// orchestration/execution infrastructure — it only delivers the request to Orchestration, which
/// runs the domain process. Mirrors the transport pattern of
/// <c>BBT.Workflow.Tasks.Executors.RemoteInvokerService</c> (Orchestration → Execution).
/// </summary>
public interface IOrchestrationForwarder
{
    /// <summary>
    /// Invokes <paramref name="route"/> on the Orchestration app with <paramref name="body"/>.
    /// Adds the <c>X-Workflow</c> header (domain/workflow/version/instance) so Orchestration
    /// resolves the correct flow schema. Throws only on transient failures (service unreachable or a
    /// transient status: 5xx, 408, 429) so the inbox processor re-delivers (at-least-once);
    /// non-transient error responses (e.g. 4xx) are logged and ignored — except a SIGNED forward (one given a
    /// <c>runtimeCallTarget</c>) that the Orchestration's sub-endpoint gate refuses (403 <c>Authorization:110001</c>),
    /// which is re-delivered.
    /// Pass <c>runtimeCallTarget</c> when the route is a gated runtime-internal endpoint: the request
    /// then carries a runtime-caller token for that target (vnext#1265).
    /// </summary>
    Task ForwardAsync<TBody>(
        HttpMethod method,
        string route,
        TBody body,
        string domain,
        string workflow,
        string? version,
        Guid instanceId,
        CancellationToken cancellationToken,
        RuntimeCallTarget? runtimeCallTarget = null);
}
