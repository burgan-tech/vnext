using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Monitor.Stats.DTOs;

/// <summary>Input for workflow/domain instance counters.</summary>
public sealed class MonitorGetInstanceCountersInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>Optional workflow key; null = count across the domain.</summary>
    public string? Workflow { get; set; }

    /// <summary>
    /// Optional workflow version filter (e.g. "1.0.0").
    /// When provided, only instances started with that workflow version are counted.
    /// Ignored when <see cref="Workflow"/> is null (domain-wide query).
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Optional GraphQL/legacy filter applied to the domain-wide count (ignored when <see cref="Workflow"/> is set).
    /// The primary use is a <c>createdAt</c> date-range to bound how much data is scanned.
    /// When the filter does not constrain <c>createdAt</c>, a default "last 7 days" window is AND-merged.
    /// </summary>
    public string? Filter { get; set; }
}

/// <summary>Input for the live state distribution query.</summary>
public sealed class MonitorGetStateDistributionInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>
    /// Optional workflow version filter (e.g. "1.0.0").
    /// When provided, only instances started with that workflow version are included in the distribution.
    /// </summary>
    public string? Version { get; set; }
}

/// <summary>Workflow-scoped stats query (T2: P10–P13).</summary>
public sealed class MonitorGetWorkflowStatsInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;
}
