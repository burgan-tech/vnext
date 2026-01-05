namespace BBT.Workflow.BackgroundJobs.Payloads;

/// <summary>
/// Represents the payload data for transition timer jobs.
/// This class contains information needed to execute a specific workflow transition
/// after a configured time delay.
/// </summary>
public sealed class TransitionTimerPayload : ITraceableJobPayload
{
    public string JobName { get; set; }
    /// <summary>
    /// Gets or sets the domain context for the workflow instance.
    /// </summary>
    /// <value>A string representing the workflow domain.</value>
    public string Domain { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier of the workflow instance.
    /// </summary>
    /// <value>A Guid representing the workflow instance ID.</value>
    public Guid InstanceId { get; set; }

    /// <summary>
    /// Gets or sets the name of the workflow definition.
    /// </summary>
    /// <value>A string representing the workflow name.</value>
    public string FlowName { get; set; }

    /// <summary>
    /// Gets or sets the version of the workflow definition.
    /// </summary>
    /// <value>A string representing the workflow version.</value>
    public string Version { get; set; }

    /// <summary>
    /// Gets or sets the key identifying the specific transition to execute.
    /// </summary>
    /// <value>A string representing the transition key.</value>
    public string TransitionKey { get; set; }

    /// <summary>
    /// Gets or sets the W3C Trace Context traceparent header for distributed tracing correlation.
    /// Format: {version}-{trace-id}-{parent-id}-{trace-flags}
    /// </summary>
    public string? TraceParent { get; set; }

    /// <summary>
    /// Gets or sets the W3C Trace Context tracestate header for vendor-specific trace data.
    /// </summary>
    public string? TraceState { get; set; }
}