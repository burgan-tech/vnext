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
