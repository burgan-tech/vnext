namespace BBT.Workflow.BackgroundJobs.Payloads;

/// <summary>
/// Payload for the long-poll acknowledge fallback job. When a state pauses the pipeline for
/// declarative long-poll termination, a delayed job carrying this payload is scheduled; if the
/// client never acknowledges within the fallback window, the job resumes the pipeline.
/// The <see cref="AckToken"/> guards against a stale fallback resuming an instance that was
/// already advanced by an acknowledge.
/// </summary>
public sealed class LongPollAckTimeoutPayload : ITraceableJobPayload
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

    /// <summary>The acknowledge token armed when the pipeline paused; used for idempotency.</summary>
    public Guid AckToken { get; set; }

    /// <summary>W3C traceparent for distributed tracing correlation.</summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C tracestate for vendor-specific trace data.</summary>
    public string? TraceState { get; set; }
}
