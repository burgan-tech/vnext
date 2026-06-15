using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Input for retrieving instance view
/// </summary>
public sealed class GetViewInput : IHasDomain
{
    [Required]
    [StringLength(WorkflowConstants.MaxDomainLength)]
    public string Domain { get; set; } = string.Empty;

    [Required]
    [StringLength(WorkflowConstants.MaxFlowLength)]
    public string Workflow { get; set; } = string.Empty;

    [StringLength(WorkflowConstants.MaxVersionLength)]
    public string? Version { get; set; } = string.Empty;

    [Required] 
    public string Instance { get; set; } = string.Empty;

    /// <summary>
    /// Request headers for rule evaluation (e.g., x-platform, x-user-role)
    /// </summary>
    public Dictionary<string, string?>? Headers { get; set; }

    /// <summary>
    /// Query parameters for rule evaluation
    /// </summary>
    public Dictionary<string, string?>? QueryParameters { get; set; }

    /// <summary>
    /// Caller role for wizard view transition filtering.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Caller roles, used to enforce state/workflow queryRoles visibility.
    /// </summary>
    public IReadOnlyList<string>? Roles { get; set; }
}
