using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Input for retrieving instance state with combined information
/// </summary>
public sealed class GetInstanceStateInput : IHasDomain
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
    /// Caller role for filtering available transitions by transition role grants.
    /// When set, only transitions allowed for this role are returned; when null or empty, all available transitions are returned.
    /// </summary>
    public string? Role { get; set; }
}
