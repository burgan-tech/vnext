namespace BBT.Workflow.Tasks;

/// <summary>
/// Trace context for distributed tracing of task execution.
/// Contains information needed for observability, correlation, and placeholder resolution.
/// </summary>
public sealed record TaskTraceContext
{
    /// <summary>
    /// Instance ID being processed.
    /// </summary>
    public Guid InstanceId { get; init; }
    
    /// <summary>
    /// Domain of the workflow.
    /// </summary>
    public string Domain { get; init; } = string.Empty;
    
    /// <summary>
    /// Key of the workflow.
    /// </summary>
    public string WorkflowKey { get; init; } = string.Empty;
    
    /// <summary>
    /// Version of the workflow.
    /// </summary>
    public string WorkflowVersion { get; init; } = string.Empty;
    
    /// <summary>
    /// Optional correlation ID for cross-service tracing.
    /// Carries the originating request id (X-Request-Id) end to end.
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

    /// Gateway-authenticated primary subject propagated for dependency log correlation.
    /// </summary>
    public string? Sub { get; init; }

    /// <summary>
    /// Gateway-authenticated actor subject propagated for dependency log correlation.
    /// </summary>
    public string? ActSub { get; init; }

    /// <summary>
    /// Original HTTP request headers forwarded from orchestration.
    /// </summary>
    public IReadOnlyDictionary<string, string>? RequestHeaders { get; init; }

    /// <summary>
    /// Serialized instance latest data JSON for placeholder resolution.
    /// </summary>
    public string? InstanceDataJson { get; init; }
    
    /// <summary>
    /// Creates a trace context from workflow and instance information.
    /// </summary>
    public static TaskTraceContext Create(
        Guid instanceId,
        string domain,
        string workflowKey,
        string workflowVersion,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? instanceDataJson = null,
        string? traceParent = null,
        string? traceState = null,
        string? sub = null,
        string? actSub = null) => new()
    {
        InstanceId = instanceId,
        Domain = domain,
        WorkflowKey = workflowKey,
        WorkflowVersion = workflowVersion,
        CorrelationId = correlationId,
        Sub = sub,
        ActSub = actSub,
        RequestHeaders = headers,
        InstanceDataJson = instanceDataJson,
        TraceParent = traceParent,
        TraceState = traceState
    };
}
