using System.ComponentModel.DataAnnotations;

using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Input for retrieving a single instance with optional extensions
/// </summary>
public sealed class GetInstanceInput : IHasDomain
{
    [Required]
    [StringLength(WorkflowConstants.MaxDomainLength)]
    public string Domain { get; set; } = string.Empty;

    [Required] 
    [StringLength(WorkflowConstants.MaxFlowLength)]
    public string Workflow { get; set; } = string.Empty;

    [Required]
    public string Instance { get; set; } = string.Empty;

    /// <summary>
    /// Extensions requested for data enrichment
    /// </summary>
    public string[]? Extensions { get; set; }

    /// <summary>
    /// ETag value for conditional requests (If-None-Match header)
    /// </summary>
    public string? IfNoneMatch { get; set; }

    /// <summary>
    /// Optional instance data version. When null or empty, latest data is used.
    /// When set, instance data is resolved to that version (semantic/partial match supported).
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// HTTP headers from the request for script context binding
    /// </summary>
    public Dictionary<string, string?>? Headers { get; set; }

    /// <summary>
    /// Query parameters from the request for script context binding
    /// </summary>
    public Dictionary<string, string?>? QueryParameters { get; set; }
}

/// <summary>
/// Input for retrieving multiple instances with pagination and optional extensions
/// </summary>
public sealed class GetInstanceListInput : IHasDomain
{
    [Required]
    [StringLength(WorkflowConstants.MaxDomainLength)]
    public string Domain { get; set; } = string.Empty;

    [Required]
    [StringLength(WorkflowConstants.MaxFlowLength)]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>
    /// Extensions requested for data enrichment
    /// </summary>
    public string[]? Extension { get; set; }

    /// <summary>
    /// Filter to apply to the query (JSON format, e.g. GraphQL-style or legacy)
    /// </summary>
    public string? Filter { get; set; }

    /// <summary>
    /// Page number for pagination (1-based)
    /// </summary>
    [Range(1, 1000)]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Page size for pagination
    /// </summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 10;

    public string PageUrl { get; set; } = string.Empty;

    /// <summary>
    /// OrderBy JSON for sorting. Single: {"field":"createdAt","direction":"desc"}.
    /// Multiple: {"fields":[{"field":"status","direction":"asc"},{"field":"createdAt","direction":"desc"}]}.
    /// Instance columns: createdAt, modifiedAt, completedAt, status, key, currentState (or state). JSON: attributes.fieldName, attributes.nested.path.
    /// </summary>
    public string? Sort { get; set; }

    /// <summary>
    /// Optional instance data version. When null or empty, latest data is used.
    /// When set, instance data is resolved to that version (semantic/partial match supported).
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// HTTP headers from the request for script context binding
    /// </summary>
    public Dictionary<string, string?>? Headers { get; set; }

    /// <summary>
    /// Query parameters from the request for script context binding
    /// </summary>
    public Dictionary<string, string?>? QueryParameters { get; set; }

    /// <summary>
    /// Gets the groupBy parameter from QueryParameters
    /// </summary>
    public string? GroupBy => QueryParameters?.TryGetValue("groupBy", out var value) == true ? value : null;

    /// <summary>
    /// Gets the aggregations parameter from QueryParameters
    /// </summary>
    public string? Aggregations => QueryParameters?.TryGetValue("aggregations", out var value) == true ? value : null;
}

/// <summary>
/// Input for retrieving instance history (all data transitions)
/// </summary>
public sealed class GetInstanceHistoryInput : IHasDomain
{
    [Required]
    [StringLength(WorkflowConstants.MaxDomainLength)]
    public string Domain { get; set; } = string.Empty;

    [Required]
    [StringLength(WorkflowConstants.MaxFlowLength)]
    public string Workflow { get; set; } = string.Empty;

    [Required]
    public string Instance { get; set; } = string.Empty;

    /// <summary>
    /// Extensions requested for data enrichment
    /// </summary>
    public string[]? Extensions { get; set; }

    /// <summary>
    /// Optional instance data version. When null or empty, latest data is used.
    /// When set, instance data is resolved to that version (semantic/partial match supported).
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// HTTP headers from the request for script context binding
    /// </summary>
    public Dictionary<string, string?>? Headers { get; set; }

    /// <summary>
    /// Query parameters from the request for script context binding
    /// </summary>
    public Dictionary<string, string?>? QueryParameters { get; set; }
}

/// <summary>
/// Input for retrieving instance data (attributes only)
/// </summary>
public sealed class GetInstanceDataInput : IHasDomain
{
    [Required]
    [StringLength(WorkflowConstants.MaxDomainLength)]
    public string Domain { get; set; } = string.Empty;

    [Required]
    [StringLength(WorkflowConstants.MaxFlowLength)]
    public string Workflow { get; set; } = string.Empty;

    [Required]
    public string Instance { get; set; } = string.Empty;

    /// <summary>
    /// ETag value for conditional requests (If-None-Match header)
    /// </summary>
    public string? IfNoneMatch { get; set; }

    /// <summary>
    /// Optional instance data version. When null or empty, latest data is used.
    /// When set, instance data is resolved to that version (semantic/partial match supported).
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Extensions requested for data enrichment
    /// </summary>
    public string[]? Extensions { get; set; }

    /// <summary>
    /// HTTP headers from the request for script context binding
    /// </summary>
    public Dictionary<string, string?>? Headers { get; set; }

    /// <summary>
    /// Query parameters from the request for script context binding
    /// </summary>
    public Dictionary<string, string?>? QueryParameters { get; set; }
} 