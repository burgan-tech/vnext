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
