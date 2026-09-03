using System.Text.Json;

namespace BBT.Workflow.BackgroundJobs.Payloads;

/// <summary>
/// Payload for the state-level notification dispatch job. When a settled state declares one or more
/// <c>notifications</c> entries, a one-shot job carrying this payload is scheduled at settle time;
/// the job re-loads the (committed) instance, rebuilds a <c>ScriptContext</c> from the carried
/// request context (<see cref="Headers"/> / <see cref="RouteValues"/> / <see cref="Data"/>), evaluates
/// each entry's rule, and dispatches the applicable slim state notifications — off the request thread.
/// </summary>
public sealed class StateNotifyPayload : ITraceableJobPayload
{
    public string JobName { get; set; }

    /// <summary>The domain context for the workflow instance.</summary>
    public string Domain { get; set; }

    /// <summary>The unique identifier of the workflow instance.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>The workflow definition key.</summary>
    public string FlowName { get; set; }

    /// <summary>The workflow definition version.</summary>
    public string Version { get; set; }

    /// <summary>The settled state key whose <c>notifications</c> entries are dispatched.</summary>
    public string StateKey { get; set; }

    /// <summary>
    /// Request headers captured at settle time. Carried so rule and mapping scripts can read them
    /// from the rebuilt <c>ScriptContext</c> in the durable job.
    /// </summary>
    public Dictionary<string, string?> Headers { get; set; } = new();

    /// <summary>
    /// Request route values captured at settle time. Fed into the rebuilt <c>ScriptContext</c>.
    /// </summary>
    public Dictionary<string, string?> RouteValues { get; set; } = new();

    /// <summary>
    /// Request body/instance data (as JSON) captured at settle time. Fed into the rebuilt
    /// <c>ScriptContext</c> body so rule and mapping scripts process the full request payload.
    /// </summary>
    public JsonElement? Data { get; set; }

    /// <summary>W3C traceparent for distributed tracing correlation.</summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C tracestate for vendor-specific trace data.</summary>
    public string? TraceState { get; set; }

    /// <summary>
    /// Trace lane anchor of the settling request, so the notify job renders as a flat lane item
    /// beside the transition hops instead of nesting under the hop that scheduled it. Null from an
    /// older build ⇒ the handler falls back to continuing the predecessor's trace.
    /// </summary>
    public string? TraceRoot { get; set; }

    /// <summary>The enclosing lane's anchor, carried for symmetry with the transition payload.</summary>
    public string? ParentTraceRoot { get; set; }
}
