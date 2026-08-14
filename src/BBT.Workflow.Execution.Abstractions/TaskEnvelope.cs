using System.Text.Json;

namespace BBT.Workflow.Execution;

/// <summary>
/// Raw task envelope for deserialization routing.
/// Contains the task type and binding as raw JSON for dynamic deserialization.
/// </summary>
public sealed class TaskEnvelope
{
    /// <summary>
    /// Task type discriminator for invoker resolution (e.g., "http", "daprservice").
    /// </summary>
    public required string TaskType { get; init; }
    
    /// <summary>
    /// Version of the binding schema.
    /// </summary>
    public string Version { get; init; } = "1.0.0";
    
    /// <summary>
    /// Task key for logging and tracing.
    /// </summary>
    public string TaskKey { get; init; }
    
    /// <summary>
    /// Raw binding configuration as JsonElement for dynamic deserialization.
    /// The actual type depends on TaskType.
    /// </summary>
    public required JsonElement Binding { get; init; }
}

/// <summary>
/// Trace context for distributed tracing and placeholder resolution.
/// Carried from Orchestration to Execution via Dapr service invocation.
/// </summary>
public sealed class TaskTraceContext
{
    /// <summary>
    /// Instance ID for tracing.
    /// </summary>
    public Guid InstanceId { get; init; }
    
    /// <summary>
    /// Workflow domain for tracing.
    /// </summary>
    public string Domain { get; init; } = string.Empty;
    
    /// <summary>
    /// Workflow key for tracing.
    /// </summary>
    public string WorkflowKey { get; init; } = string.Empty;
    
    /// <summary>
    /// Workflow version for tracing.
    /// </summary>
    public string? WorkflowVersion { get; init; }

    /// <summary>
    /// Original HTTP request headers forwarded from orchestration.
    /// Used for <c>{HEADER.*}</c> placeholder resolution.
    /// </summary>
    public IReadOnlyDictionary<string, string>? RequestHeaders { get; init; }

    /// <summary>
    /// Serialized instance latest data JSON for placeholder resolution.
    /// Used for <c>{INSTANCE.*}</c> placeholder resolution.
    /// </summary>
    public string? InstanceDataJson { get; init; }

    /// <summary>
    /// Originating request id (X-Request-Id value) for cross-service log correlation.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// W3C traceparent captured at invoke time on the orchestration side.
    /// Fallback for restoring the trace tree when transport-level propagation is unavailable.
    /// </summary>
    public string? TraceParent { get; init; }

    /// <summary>
    /// W3C tracestate accompanying <see cref="TraceParent"/>.
    /// </summary>
    public string? TraceState { get; init; }
}

/// <summary>
/// Request wrapper containing envelope and trace context.
/// </summary>
public sealed class TaskInvokeRequest
{
    /// <summary>
    /// The task envelope containing type, version, and binding.
    /// </summary>
    public required TaskEnvelope Envelope { get; init; }
    
    /// <summary>
    /// Trace context for distributed tracing.
    /// </summary>
    public TaskTraceContext? TraceContext { get; init; }
}

