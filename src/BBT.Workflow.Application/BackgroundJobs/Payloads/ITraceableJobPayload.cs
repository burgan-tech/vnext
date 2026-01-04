namespace BBT.Workflow.BackgroundJobs.Payloads;

/// <summary>
/// Defines a contract for job payloads that support distributed tracing.
/// Implementing this interface allows background job handlers to restore
/// trace context and enrich activities with common observability data.
/// </summary>
public interface ITraceableJobPayload
{
    /// <summary>
    /// Gets the unique name of the background job.
    /// </summary>
    string JobName { get; }

    /// <summary>
    /// Gets the domain context for the workflow instance.
    /// </summary>
    string Domain { get; }

    /// <summary>
    /// Gets the unique identifier of the workflow instance.
    /// </summary>
    Guid InstanceId { get; }

    /// <summary>
    /// Gets the name of the workflow definition.
    /// </summary>
    string FlowName { get; }

    /// <summary>
    /// Gets the version of the workflow definition.
    /// </summary>
    string? Version { get; }

    /// <summary>
    /// Gets the W3C Trace Context traceparent header for distributed tracing correlation.
    /// Format: {version}-{trace-id}-{parent-id}-{trace-flags}
    /// </summary>
    string? TraceParent { get; }

    /// <summary>
    /// Gets the W3C Trace Context tracestate header for vendor-specific trace data.
    /// </summary>
    string? TraceState { get; }
}

