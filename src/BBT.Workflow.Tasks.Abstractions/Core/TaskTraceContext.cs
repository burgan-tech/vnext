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
    /// </summary>
    public string? CorrelationId { get; init; }

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
        string? instanceDataJson = null) => new()
    {
        InstanceId = instanceId,
        Domain = domain,
        WorkflowKey = workflowKey,
        WorkflowVersion = workflowVersion,
        CorrelationId = correlationId,
        RequestHeaders = headers,
        InstanceDataJson = instanceDataJson
    };
}

