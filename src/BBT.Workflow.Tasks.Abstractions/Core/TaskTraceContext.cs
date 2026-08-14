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
    /// Business-operation correlation identifier: the chain-stable execution correlation id
    /// (<c>WorkflowExecutionContext.CorrelationId</c>), constant across an execution chain
    /// including async job hops and auto-chained transitions. Intentionally separate from the
    /// per-request <see cref="RequestId"/> and the W3C trace identifier.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Originating request id (X-Request-Id value) for cross-service log correlation.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// W3C traceparent captured at invoke time on the orchestration side.
    /// Fallback for restoring the trace tree when transport-level propagation is unavailable.
    /// </summary>
    public string? TraceParent { get; init; }

    /// <summary>
    /// W3C tracestate accompanying <see cref="TraceParent"/>.
    /// </summary>
    public string? TraceState { get; init; }

    /// <summary>
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
        string? actSub = null,
        string? requestId = null) => new()
    {
        InstanceId = instanceId,
        Domain = domain,
        WorkflowKey = workflowKey,
        WorkflowVersion = workflowVersion,
        CorrelationId = correlationId,
        RequestId = requestId,
        Sub = sub,
        ActSub = actSub,
        RequestHeaders = headers,
        InstanceDataJson = instanceDataJson,
        TraceParent = traceParent,
        TraceState = traceState
    };
}
