using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Monitor.Jobs.DTOs;

/// <summary>Input for querying active jobs (P7). Workflow optional → domain-wide.</summary>
public sealed class MonitorGetActiveJobsInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>Optional workflow key; when null, domain-wide query (best-effort, resolved schema).</summary>
    public string? Workflow { get; set; }
}

/// <summary>Active jobs response.</summary>
public sealed class MonitorActiveJobsResponse
{
    /// <summary>List of active scheduled jobs / timers.</summary>
    public List<MonitorJobItem> Jobs { get; set; } = [];
}

/// <summary>A single active scheduled job or timer.</summary>
public sealed class MonitorJobItem
{
    /// <summary>Unique job identifier.</summary>
    public Guid JobId { get; set; }

    /// <summary>Job name (timer or scheduled task name).</summary>
    public string? Name { get; set; }

    /// <summary>The instance this job is associated with.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>Workflow (flow) key.</summary>
    public string? Flow { get; set; }

    /// <summary>Domain key.</summary>
    public string? Domain { get; set; }

    /// <summary>Whether the job is currently active.</summary>
    public bool IsActive { get; set; }

    /// <summary>When the job was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the job was last modified.</summary>
    public DateTime? ModifiedAt { get; set; }
}
