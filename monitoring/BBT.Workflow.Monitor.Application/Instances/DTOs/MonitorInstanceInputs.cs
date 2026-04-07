using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Monitor.Instances.DTOs;

/// <summary>
/// Input for retrieving a single instance by key or ID.
/// </summary>
public sealed class MonitorGetInstanceInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Instance key or ID.</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;
}

/// <summary>
/// Input for listing instances with pagination and optional GraphQL filter.
/// </summary>
public sealed class MonitorGetInstancesInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>GraphQL-style filter JSON or legacy filter string.</summary>
    public string? Filter { get; set; }

    /// <summary>OrderBy JSON. Single: {"field":"createdAt","direction":"desc"}.</summary>
    public string? Sort { get; set; }

    /// <summary>1-based page number.</summary>
    [Range(1, 1000)]
    public int Page { get; set; } = 1;

    /// <summary>Page size.</summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 10;

    /// <summary>URL of the current page for HATEOAS link generation.</summary>
    public string PageUrl { get; set; } = string.Empty;

    /// <summary>Gets the groupBy parameter from query parameters if embedded in filter.</summary>
    public string? GroupBy { get; set; }

    /// <summary>Gets the aggregations parameter from query parameters if embedded in filter.</summary>
    public string? Aggregations { get; set; }
}

/// <summary>
/// Input for retrieving instance data (latest + version history).
/// </summary>
public sealed class MonitorGetInstanceDataInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Instance key or ID.</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;
}

/// <summary>
/// Input for retrieving the state-transition history of an instance.
/// </summary>
public sealed class MonitorGetInstanceHistoryInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Instance key or ID.</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;
}

/// <summary>
/// Input for retrieving task execution records for a specific transition.
/// </summary>
public sealed class MonitorGetInstanceTaskInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Instance key or ID.</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;

    /// <summary>
    /// The transition Key whose tasks should be returned.
    /// Required to avoid full table scans across all transitions.
    /// </summary>
    [Required]
    public Guid TransitionId { get; set; }
}
