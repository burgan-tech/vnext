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
