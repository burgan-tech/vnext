using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances.DTOs;

/// <summary>
/// Input for retrieving the flow-level master schema an instance is bound to.
/// </summary>
public sealed class GetMasterInput : IHasDomain
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
    /// Request headers for queryRoles dynamic-role evaluation.
    /// </summary>
    public Dictionary<string, string?>? Headers { get; set; }

    /// <summary>
    /// Query parameters for queryRoles dynamic-role evaluation.
    /// </summary>
    public Dictionary<string, string?>? QueryParameters { get; set; }

    /// <summary>
    /// Caller roles, used to enforce state/workflow queryRoles visibility.
    /// </summary>
    public IReadOnlyList<string>? Roles { get; set; }

    /// <summary>
    /// ETag value for conditional requests (If-None-Match header).
    /// </summary>
    public string? IfNoneMatch { get; set; }
}
