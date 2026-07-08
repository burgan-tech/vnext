namespace BBT.Workflow.Monitor.Jobs.Filters;

/// <summary>Optional createdAt time-range filter for the jobs list endpoints.</summary>
public sealed class MonitorJobFilterInput
{
    /// <summary>Lower bound for job creation timestamp (inclusive, ISO 8601 UTC).</summary>
    public DateTime? CreatedAtGte { get; set; }

    /// <summary>Upper bound for job creation timestamp (inclusive, ISO 8601 UTC).</summary>
    public DateTime? CreatedAtLte { get; set; }

    /// <summary>True when no bound has been supplied.</summary>
    public bool IsEmpty => CreatedAtGte is null && CreatedAtLte is null;
}
