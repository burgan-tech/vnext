namespace BBT.Workflow.Instances;

/// <summary>Read-only aggregation read-models for monitor statistics. Additive — not used by orchestration.</summary>

/// <summary>Per-task execution aggregation across a schema.</summary>
public sealed record TaskExecutionStat(string TaskKey, int ExecutionCount, int SuccessCount, int FailureCount);

/// <summary>Per-transition execution aggregation (keyed by transition + from/to state pair) across a schema.</summary>
public sealed record TransitionExecutionStat(string TransitionKey, string? FromState, string? ToState, int Count, int CompletedCount, int ManualCount, int AutomaticCount, int ScheduledCount, int EventCount);

/// <summary>Instance count for a specific state key.</summary>
public sealed record StateCountStat(string StateKey, int Count);

/// <summary>
/// Per-status instance counts produced by a single grouped aggregation query (one SQL round-trip
/// using conditional counts), honouring an optional filter. Additive — monitor-only.
/// </summary>
public sealed record InstanceStatusCounts(long Active, long Busy, long Completed, long Faulted, long Passive)
{
    /// <summary>Sum of all status counts.</summary>
    public long Total => Active + Busy + Completed + Faulted + Passive;
}

/// <summary>Aggregate duration statistics over completed instances in a schema.</summary>
public sealed record InstanceDurationStat(double AvgMs, double MinMs, double MaxMs, long CompletedCount);

/// <summary>
/// Join projection: InstanceTask enriched with its parent transition's definition key and state context.
/// Additive — used only by monitor read paths, not by orchestration.
/// </summary>
public sealed record InstanceTaskRow(
    InstanceTask Task,
    string TransitionKey,
    string FromState,
    string? ToState,
    Definitions.TriggerType TriggerType
);

/// <summary>
/// Column projection behind the public task-history function: exactly the metadata the response
/// carries, selected in SQL so the journal's jsonb payloads never leave the database.
/// <see cref="FaultedResponseJson"/> is the one deliberate exception — the Response column, fetched
/// only when the row is Faulted (its content is then the small <c>{"error": ...}</c> object the
/// fault reason is read from), null for every other status.
/// </summary>
public sealed record InstanceTaskHistoryRow(
    Guid Id,
    string TaskKey,
    string TransitionKey,
    string FromState,
    string? ToState,
    Definitions.TriggerType TriggerType,
    Definitions.TaskStatus Status,
    Definitions.BusinessStatus BusinessStatus,
    DateTime StartedAt,
    DateTime? FinishedAt,
    TimeSpan? Duration,
    string? FaultedResponseJson
);

/// <summary>
/// Minimal identity of one task journal row, scoped to its owning instance — what the
/// action-history function needs to admit a taskId and echo the owning task.
/// </summary>
public sealed record InstanceTaskRef(Guid Id, string TaskKey);
