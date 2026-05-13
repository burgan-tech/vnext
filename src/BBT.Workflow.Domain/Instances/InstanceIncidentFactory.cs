using System.Diagnostics;

namespace BBT.Workflow.Instances;

/// <summary>
/// Factory methods for creating <see cref="InstanceIncident"/> from pipeline error context.
/// Placed in the Domain layer so both Application pipeline steps and the Instance aggregate
/// can reference it without circular dependencies.
/// </summary>
public static class InstanceIncidentFactory
{
    /// <summary>
    /// Creates an incident from structured error information available during pipeline execution.
    /// </summary>
    /// <param name="state">State where the error occurred.</param>
    /// <param name="transition">Transition that was executing.</param>
    /// <param name="taskKey">Failed task key (null for pipeline-level errors).</param>
    /// <param name="message">Error message.</param>
    /// <param name="errorCode">Normalized error code.</param>
    /// <param name="errorLayer">Error layer name (Transport/Task/Pipeline).</param>
    /// <param name="statusCode">HTTP status code if applicable.</param>
    /// <param name="stackTrace">Exception stack trace if available.</param>
    /// <param name="boundaryAction">Error boundary action taken.</param>
    /// <param name="boundaryLevel">Error boundary level that matched.</param>
    /// <param name="retryCount">Number of retry attempts.</param>
    /// <param name="traceId">Optional OpenTelemetry trace ID override. Defaults to <see cref="Activity.Current"/>.</param>
    public static InstanceIncident Create(
        string state,
        string transition,
        string? taskKey,
        string message,
        string errorCode,
        string errorLayer,
        int? statusCode = null,
        string? stackTrace = null,
        string? boundaryAction = null,
        string? boundaryLevel = null,
        int retryCount = 0,
        string? traceId = null)
    {
        return new InstanceIncident
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            State = state,
            Transition = transition,
            Task = taskKey,
            Message = TruncateMessage(message),
            ErrorCode = errorCode,
            ErrorLayer = errorLayer,
            StatusCode = statusCode,
            StackTrace = TruncateStackTrace(stackTrace),
            BoundaryAction = boundaryAction,
            BoundaryLevel = boundaryLevel,
            RetryCount = retryCount,
            TraceId = traceId ?? Activity.Current?.TraceId.ToString()
        };
    }

    private const int MaxMessageLength = 1024;
    private const int MaxStackTraceLength = 4096;

    private static string TruncateMessage(string? message)
        => message?.Length > MaxMessageLength ? message[..MaxMessageLength] : message ?? string.Empty;

    private static string? TruncateStackTrace(string? stackTrace)
        => stackTrace?.Length > MaxStackTraceLength ? stackTrace[..MaxStackTraceLength] : stackTrace;
}
