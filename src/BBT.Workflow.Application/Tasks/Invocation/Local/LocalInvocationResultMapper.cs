namespace BBT.Workflow.Tasks.Invocation.Local;

/// <summary>
/// Translates between the wire-side types the shared invocation cores speak
/// (<c>BBT.Workflow.Execution.*</c>) and the orchestrator-side twins the executor pipeline
/// consumes. This is the same translation the remote path performs when it unwraps an
/// <c>/execution/invoke</c> response — kept in one place so every local invoker maps identically.
/// </summary>
internal static class LocalInvocationResultMapper
{
    public static TaskInvocationResult ToOrchestratorResult(Execution.TaskInvocationResult result) => new()
    {
        IsSuccess = result.IsSuccess,
        StatusCode = result.StatusCode,
        Body = result.Body,
        Data = result.Data,
        ErrorMessage = result.ErrorMessage,
        Headers = result.Headers,
        Metadata = result.Metadata,
        TaskType = result.TaskType,
        ExecutionDurationMs = result.ExecutionDurationMs
    };

    /// <summary>
    /// The reverse direction, needed by <c>LocalCacheAsideTaskInvoker</c>: its source-dispatch
    /// delegate must hand the shared <c>CacheAsideInvocation</c> core a wire-side result, whether
    /// the source ran through a local invoker (which returns the orchestrator-side type) or through
    /// the remote fallback.
    /// </summary>
    public static Execution.TaskInvocationResult ToWireResult(TaskInvocationResult result) => new()
    {
        IsSuccess = result.IsSuccess,
        StatusCode = result.StatusCode,
        Body = result.Body,
        Data = result.Data,
        ErrorMessage = result.ErrorMessage,
        Headers = result.Headers,
        Metadata = result.Metadata,
        TaskType = result.TaskType,
        ExecutionDurationMs = result.ExecutionDurationMs
    };

    /// <summary>
    /// Carries only the correlation and identity fields: the cores read nothing else, and the
    /// heavy placeholder fields (request headers, instance data JSON) have no business on an
    /// in-process call.
    /// </summary>
    public static Execution.TaskTraceContext? ToWireTraceContext(TaskTraceContext? trace) =>
        trace is null ? null : new Execution.TaskTraceContext
        {
            InstanceId = trace.InstanceId,
            Domain = trace.Domain,
            WorkflowKey = trace.WorkflowKey,
            WorkflowVersion = trace.WorkflowVersion,
            CorrelationId = trace.CorrelationId,
            Sub = trace.Sub,
            ActSub = trace.ActSub,
            RequestId = trace.RequestId
        };
}
