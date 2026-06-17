namespace BBT.Workflow.Monitor.Stats.DTOs;

/// <summary>Status-based instance counters for a workflow (or domain), for dashboard widgets.</summary>
public sealed class MonitorInstanceCountersResponse
{
    /// <summary>Number of instances in Active status.</summary>
    public long Active { get; set; }

    /// <summary>Number of instances in Busy status.</summary>
    public long Busy { get; set; }

    /// <summary>Number of instances in Completed status.</summary>
    public long Completed { get; set; }

    /// <summary>Number of instances in Faulted status.</summary>
    public long Faulted { get; set; }

    /// <summary>Number of instances in Passive status.</summary>
    public long Passive { get; set; }

    /// <summary>Sum of all status counters.</summary>
    public long Total { get; set; }

    /// <summary>
    /// The effective filter actually applied to the domain-wide count (including the default
    /// "last 7 days" <c>createdAt</c> window when the caller did not specify one). Null for
    /// the workflow-scoped query, which is not filtered.
    /// </summary>
    public string? AppliedFilter { get; set; }
}

/// <summary>Live instance distribution across a workflow's states.</summary>
public sealed class MonitorStateDistributionResponse
{
    /// <summary>Per-state instance counts (one entry per state defined in the workflow).</summary>
    public List<MonitorStateCount> States { get; set; } = [];

    /// <summary>Total number of instances currently in Active status across all states.</summary>
    public long TotalActiveInstances { get; set; }
}

/// <summary>Instance counts for a single workflow state broken down by runtime status.</summary>
public sealed class MonitorStateCount
{
    /// <summary>The state key as defined in the workflow definition.</summary>
    public string? StateKey { get; set; }

    /// <summary>Total instances in this state (all statuses).</summary>
    public long Total { get; set; }

    /// <summary>Instances in this state with Active status.</summary>
    public long Active { get; set; }

    /// <summary>Instances in this state with Busy status.</summary>
    public long Busy { get; set; }

    /// <summary>Instances in this state with Faulted status.</summary>
    public long Faulted { get; set; }
}

/// <summary>P10 fault statistics: total faulted count, per-state and per-task breakdown, time-window trend.</summary>
public sealed class MonitorFaultStatsResponse
{
    /// <summary>Total number of faulted instances in the current schema.</summary>
    public long TotalFaulted { get; set; }

    /// <summary>Faulted instance counts grouped by the current state key.</summary>
    public List<MonitorKeyCount> ByState { get; set; } = [];

    /// <summary>Faulted task counts grouped by task key.</summary>
    public List<MonitorKeyCount> ByTask { get; set; } = [];

    /// <summary>Faulted instance counts over recent time windows.</summary>
    public MonitorTrend Trend { get; set; } = new();
}

/// <summary>A named count entry used in fault breakdown lists.</summary>
public sealed class MonitorKeyCount
{
    /// <summary>The key (state key, task key, etc.).</summary>
    public string? Key { get; set; }

    /// <summary>The associated count.</summary>
    public long Count { get; set; }
}

/// <summary>Faulted-instance counts over recent time windows.</summary>
public sealed class MonitorTrend
{
    /// <summary>Faulted count in the last 1 hour.</summary>
    public long Last1h { get; set; }

    /// <summary>Faulted count in the last 24 hours.</summary>
    public long Last24h { get; set; }

    /// <summary>Faulted count in the last 7 days.</summary>
    public long Last7d { get; set; }
}

/// <summary>P11 task execution statistics: per-task counts and success/failure rates.</summary>
public sealed class MonitorTaskStatsResponse
{
    /// <summary>All tasks with their execution statistics.</summary>
    public List<MonitorTaskStatItem> ByTask { get; set; } = [];
}

/// <summary>Execution statistics for a single task key.</summary>
public sealed class MonitorTaskStatItem
{
    /// <summary>The task definition key.</summary>
    public string? TaskKey { get; set; }

    /// <summary>Total number of executions.</summary>
    public int ExecutionCount { get; set; }

    /// <summary>Ratio of successful executions to total (0–1).</summary>
    public double SuccessRate { get; set; }

    /// <summary>Ratio of failed executions to total (0–1).</summary>
    public double FailureRate { get; set; }
}

/// <summary>P12 instance completion duration statistics: avg/min/max ms and completed count.</summary>
public sealed class MonitorDurationStatsResponse
{
    /// <summary>Average completion duration in milliseconds.</summary>
    public double AvgMs { get; set; }

    /// <summary>Minimum completion duration in milliseconds.</summary>
    public double MinMs { get; set; }

    /// <summary>Maximum completion duration in milliseconds.</summary>
    public double MaxMs { get; set; }

    /// <summary>Number of completed instances with recorded duration.</summary>
    public long CompletedCount { get; set; }
}

/// <summary>P13 transition execution statistics: per-transition counts, durations, completion rates, and trigger breakdowns.</summary>
public sealed class MonitorTransitionStatsResponse
{
    /// <summary>Per-transition aggregated statistics.</summary>
    public List<MonitorTransitionStatItem> ByTransition { get; set; } = [];

    /// <summary>State-to-state flow density (from/to pair counts).</summary>
    public List<MonitorFlowDensity> FlowDensity { get; set; } = [];
}

/// <summary>Aggregated statistics for a single transition key.</summary>
public sealed class MonitorTransitionStatItem
{
    /// <summary>The transition definition key.</summary>
    public string? TransitionKey { get; set; }

    /// <summary>Total number of executions for this transition.</summary>
    public int Count { get; set; }

    /// <summary>Ratio of completed executions to total (0–1).</summary>
    public double CompletionRate { get; set; }

    /// <summary>Breakdown of trigger types for this transition.</summary>
    public MonitorTriggerBreakdown TriggerTypeBreakdown { get; set; } = new();
}

/// <summary>Count of transitions grouped by trigger type.</summary>
public sealed class MonitorTriggerBreakdown
{
    /// <summary>Manual trigger count.</summary>
    public int Manual { get; set; }

    /// <summary>Automatic trigger count.</summary>
    public int Automatic { get; set; }

    /// <summary>Scheduled trigger count.</summary>
    public int Scheduled { get; set; }

    /// <summary>Event trigger count.</summary>
    public int Event { get; set; }
}

/// <summary>Instance count for a state-to-state transition pair (flow density).</summary>
public sealed class MonitorFlowDensity
{
    /// <summary>The originating state key.</summary>
    public string? FromState { get; set; }

    /// <summary>The target state key.</summary>
    public string? ToState { get; set; }

    /// <summary>Number of transitions on this path.</summary>
    public int Count { get; set; }
}
