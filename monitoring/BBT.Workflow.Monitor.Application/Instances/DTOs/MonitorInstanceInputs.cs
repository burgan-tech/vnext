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

    /// <summary>Optional: return a specific data version instead of the latest + history.</summary>
    public string? Version { get; set; }
}

/// <summary>
/// Input for the unified instance timeline endpoint.
/// <para>
/// Behaviour depends on the optional identifiers:
/// <list type="bullet">
/// <item>No identifier → the full ordered transition timeline (optionally with embedded tasks).</item>
/// <item><see cref="TransitionId"/> → details of that single transition (with its tasks when <see cref="IncludeTasks"/> is true).</item>
/// <item><see cref="TaskId"/> → that single task execution record.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MonitorGetInstanceTimelineInput : IHasDomain
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
    /// When provided, returns only the matching transition's details instead of the full timeline.
    /// </summary>
    public Guid? TransitionId { get; set; }

    /// <summary>
    /// When provided, returns only the matching single task execution record.
    /// Takes precedence over <see cref="TransitionId"/>.
    /// </summary>
    public Guid? TaskId { get; set; }

    /// <summary>
    /// When true, embeds task records into each returned transition.
    /// Ignored in single-task mode.
    /// </summary>
    public bool IncludeTasks { get; set; }
}

/// <summary>Input for the instance state query (current state + available transitions + active sub-flows).</summary>
public sealed class MonitorGetInstanceStateInput : IHasDomain
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

/// <summary>Input for the fault detail query.</summary>
public sealed class MonitorGetInstanceFaultsInput : IHasDomain
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

/// <summary>Input for the recursive instance hierarchy query.</summary>
public sealed class MonitorGetInstanceHierarchyInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Instance key or ID (root of the hierarchy).</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;
}

/// <summary>Input for the instance data diff between two versions.</summary>
public sealed class MonitorGetInstanceDataDiffInput : IHasDomain
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

    /// <summary>The baseline data version (older).</summary>
    [Required]
    public string From { get; set; } = string.Empty;

    /// <summary>The target data version (newer).</summary>
    [Required]
    public string To { get; set; } = string.Empty;
}

/// <summary>Input for the instance view query (P1).</summary>
public sealed class MonitorGetInstanceViewInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>The instance business key or GUID.</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;

    /// <summary>Optional: resolve the view of a specific transition instead of the current state.</summary>
    public string? TransitionKey { get; set; }

    /// <summary>Optional role context (reserved; no rule evaluation performed).</summary>
    public string? Role { get; set; }

    /// <summary>Optional workflow/definition version.</summary>
    public string? Version { get; set; }
}

/// <summary>Input for parent reverse navigation (P6).</summary>
public sealed class MonitorGetParentInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required] public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow key.</summary>
    [Required] public string Workflow { get; set; } = string.Empty;

    /// <summary>The sub-flow instance identifier (business key or GUID).</summary>
    [Required] public string Instance { get; set; } = string.Empty;
}
