using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Input for retrieving the instance-correlation tree (recursive, parent -> child, over the
/// instance's correlated subflow/subprocess children).
/// </summary>
public sealed class GetInstanceCorrelationInput : IHasDomain
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
