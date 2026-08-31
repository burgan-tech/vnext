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

    /// <summary>
    /// The trace lane anchor (W3C traceparent) this job's span must be parented to, so that every
    /// hop of the same instance appears as a SIBLING rather than nested inside its predecessor.
    /// See <c>WorkflowTraceLane</c>.
    /// <para>
    /// A default interface member, deliberately: only jobs that fire immediately after enqueue take
    /// part in a lane. Deferred payloads (timer, timeout, long-poll ack) inherit the null and keep
    /// the ambient-parent policy — resurrecting an hours-old anchor would produce an hours-long trace.
    /// Returning null here is also what makes a payload serialized by an older build degrade to the
    /// pre-lane behaviour instead of failing.
    /// </para>
    /// </summary>
    string? TraceRoot => null;

    /// <summary>
    /// The enclosing lane's anchor — set only while executing a subflow, so the eventual resume can
    /// return to the parent instance's lane instead of nesting under the subflow's.
    /// </summary>
    string? ParentTraceRoot => null;
}

