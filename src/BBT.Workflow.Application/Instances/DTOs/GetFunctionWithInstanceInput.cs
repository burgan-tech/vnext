using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Input for retrieving function result for an instance
/// </summary>
public sealed class GetFunctionWithInstanceInput : IHasDomain
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
    /// ETag for conditional requests (If-None-Match). When set, state endpoint may return 304.
    /// </summary>
    public string? IfNoneMatch { get; set; }

    /// <summary>
    /// Version of the workflow
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Extensions to be appended to the data href URL
    /// </summary>
    public string[]? Extensions { get; set; }

    public Dictionary<string, string?> Headers { get; set; }

    public Dictionary<string, string?> QueryParams { get; set; }

    /// <summary>
    /// Caller role for state function (e.g. to filter available transitions by transition role grants when calling SubFlow state).
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Caller roles forwarded to the locally-routed function for authorization (e.g. instance query
    /// access checks). Mirrors the single <see cref="Role"/> but carries the full role list.
    /// </summary>
    public IReadOnlyList<string>? Roles { get; set; }
}

