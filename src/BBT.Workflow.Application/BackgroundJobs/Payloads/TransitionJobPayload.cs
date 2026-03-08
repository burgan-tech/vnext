using System.Text.Json;
using BBT.Workflow.Shared;

namespace BBT.Workflow.BackgroundJobs.Payloads;

/// <summary>
/// Payload for asynchronous transition background jobs.
/// Contains all necessary information to execute a workflow transition in the background.
/// </summary>
public sealed class TransitionJobPayload : ITraceableJobPayload
{
    public string JobName { get; set; }
    
    /// <summary>
    /// Gets or sets the instance ID for the transition.
    /// </summary>
    public Guid InstanceId { get; set; }
    
    /// <summary>
    /// Gets or sets the transition key to execute.
    /// </summary>
    public string TransitionKey { get; set; } = default!;
    
    /// <summary>
    /// Gets or sets the domain name for the workflow.
    /// </summary>
    public string Domain { get; set; } = default!;
    
    /// <summary>
    /// Gets or sets the workflow name.
    /// </summary>
    public string Workflow { get; set; } = default!;

    /// <summary>
    /// Gets the workflow name for tracing purposes.
    /// Maps to <see cref="Workflow"/> property for ITraceableJobPayload compatibility.
    /// </summary>
    string ITraceableJobPayload.FlowName => Workflow;

    /// <summary>
    /// Gets or sets the workflow version.
    /// </summary>
    public string Version { get; set; } = default!;
    
    /// <summary>
    /// Gets or sets the transition data as JSON.
    /// </summary>
    public JsonElement? Data { get; set; }

    /// <summary>
    /// Gets or sets the instance key (Optional).
    /// </summary>
    public string? InstanceKey { get; set; }

    /// <summary>
    /// Gets or sets the instance tags (Optional).
    /// </summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// Gets or sets the request headers.
    /// </summary>
    public Dictionary<string, string?> Headers { get; set; } = new();
    
    /// <summary>
    /// Gets or sets the route values.
    /// </summary>
    public Dictionary<string, string?> RouteValues { get; set; } = new();
    
    /// <summary>
    /// Gets or sets the execution context for the transition.
    /// </summary>
    public ExecutionActor ExecutionActor { get; set; } = ExecutionActor.User;

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
