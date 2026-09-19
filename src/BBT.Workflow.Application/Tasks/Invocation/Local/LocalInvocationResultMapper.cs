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
    /// Reads the exception type name the shared invocation cores stamp into
    /// <c>Metadata["ExceptionType"]</c> on their unhandled-exception path. The cores swallow the
    /// exception themselves by contract (they return a <see cref="Execution.TaskInvocationResult"/>,
    /// never throw), so this is both the only way a local invoker's error log can still name the
    /// failure type, and the signal that distinguishes a genuine thrown exception from a returned
    /// validation failure (missing key, unsupported command, unresolvable store name — which sets
    /// no such key). Mirrors the gate the Execution host's own invokers apply (e.g.
    /// <c>StateStoreTaskInvoker.TryGetExceptionType</c>): a returned validation failure is metered,
    /// never logged at Error, on either host — the Execution host was the live production path
    /// when that reasoning was written, and the local path is now the live one, so the two must
    /// stay silent on the same definition errors.
    /// </summary>
    public static bool HasExceptionType(Execution.TaskInvocationResult result, out string exceptionType)
    {
        if (result.Metadata?.TryGetValue("ExceptionType", out var value) == true && value is string type)
        {
            exceptionType = type;
            return true;
        }

        exceptionType = string.Empty;
        return false;
    }

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
