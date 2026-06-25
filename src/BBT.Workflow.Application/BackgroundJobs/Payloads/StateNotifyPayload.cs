namespace BBT.Workflow.BackgroundJobs.Payloads;

/// <summary>
/// Payload for the state-level notification dispatch job. When a settled state declares a
/// <c>notification</c> directive, a one-shot job carrying this payload is scheduled at settle time;
/// the job re-loads the (committed) instance, resolves the state's notify mapping from the workflow
/// definition and dispatches the slim state notification — off the request thread.
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

    /// <summary>The settled state key whose <c>notification</c> directive is dispatched.</summary>
    public string StateKey { get; set; }

    /// <summary>W3C traceparent for distributed tracing correlation.</summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C tracestate for vendor-specific trace data.</summary>
    public string? TraceState { get; set; }
}
