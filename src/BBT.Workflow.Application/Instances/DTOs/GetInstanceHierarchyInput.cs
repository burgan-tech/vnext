using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Input for retrieving instance hierarchy (recursive tree of subflow/subprocess children).
/// </summary>
public sealed class GetInstanceHierarchyInput : IHasDomain
{
    /// <summary>
    /// Domain.
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxDomainLength)]
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Workflow (flow) name.
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxFlowLength)]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>
    /// Instance key or ID.
    /// </summary>
    [Required]
    public string Instance { get; set; } = string.Empty;
}
