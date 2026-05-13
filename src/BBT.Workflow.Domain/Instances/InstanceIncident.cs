using System.Text.Json.Serialization;

namespace BBT.Workflow.Instances;

/// <summary>
/// Value object representing an error boundary incident on a workflow instance.
/// Captures structured error details when an error boundary triggers, enabling
/// incident tracking, retry resolution detection, and script-level error awareness.
/// </summary>
/// <remarks>
/// Stored as part of a JSONB array on the <see cref="Instance"/> aggregate.
/// Incidents are created by the pipeline when error boundaries resolve an action,
/// and resolved when the instance is successfully retried or the error-boundary
/// transition completes.
/// </remarks>
public sealed class InstanceIncident
{
    /// <summary>Unique incident identifier.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>When the incident occurred (UTC).</summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    /// <summary>State where the error occurred.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = default!;

    /// <summary>Transition that was executing when the error occurred.</summary>
    [JsonPropertyName("transition")]
    public string Transition { get; init; } = default!;

    /// <summary>Task key that failed (null for pipeline-level errors).</summary>
    [JsonPropertyName("task")]
    public string? Task { get; init; }

    /// <summary>Human-readable error message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = default!;

    /// <summary>Exception stack trace (when available from transport/pipeline errors).</summary>
    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    /// <summary>OpenTelemetry trace identifier for correlation with distributed traces.</summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    /// <summary>Normalized error code (e.g. "Task:Http:503").</summary>
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; init; } = default!;

    /// <summary>Error layer: "Transport", "Task", or "Pipeline".</summary>
    [JsonPropertyName("errorLayer")]
    public string ErrorLayer { get; init; } = default!;

    /// <summary>HTTP status code (when applicable).</summary>
    [JsonPropertyName("statusCode")]
    public int? StatusCode { get; init; }

    /// <summary>Error boundary action taken (e.g. "Abort", "Retry", "Rollback", "Notify", "Log", "Ignore").</summary>
    [JsonPropertyName("boundaryAction")]
    public string? BoundaryAction { get; init; }

    /// <summary>Error boundary level that matched (e.g. "Task", "State", "Global").</summary>
    [JsonPropertyName("boundaryLevel")]
    public string? BoundaryLevel { get; init; }

    /// <summary>Whether this incident has been resolved (by retry or successful error-boundary transition).</summary>
    [JsonPropertyName("isResolved")]
    public bool IsResolved { get; private set; }

    /// <summary>When the incident was resolved (UTC).</summary>
    [JsonPropertyName("resolvedAt")]
    public DateTime? ResolvedAt { get; private set; }

    /// <summary>Number of retry attempts before resolution or exhaustion.</summary>
    [JsonPropertyName("retryCount")]
    public int RetryCount { get; init; }

    /// <summary>
    /// Marks this incident as resolved.
    /// </summary>
    public void Resolve()
    {
        if (IsResolved)
            return;

        IsResolved = true;
        ResolvedAt = DateTime.UtcNow;
    }

    /// <summary>Maximum number of incidents retained per instance to prevent unbounded JSONB growth.</summary>
    public const int MaxRetainedIncidents = 5;
}
