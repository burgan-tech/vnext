using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances.DTOs;

/// <summary>
/// Input for retrieving instance extensions
/// </summary>
public sealed class GetExtensionsInput : IHasDomain
{
    /// <summary>
    /// Domain name
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxDomainLength)]
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Workflow name
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxFlowLength)]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>
    /// Version of the workflow
    /// </summary>
    [StringLength(WorkflowConstants.MaxVersionLength)]
    public string? Version { get; set; }

    /// <summary>
    /// Instance identifier (ID or Key)
    /// </summary>
    [Required]
    public string Instance { get; set; } = string.Empty;

    /// <summary>
    /// Extensions to execute
    /// </summary>
    public string[]? Extensions { get; init; }

    /// <summary>
    /// Request headers for script context
    /// </summary>
    public Dictionary<string, string?> Headers { get; set; } = new();

    /// <summary>
    /// Query parameters for script context
    /// </summary>
    public Dictionary<string, string?> QueryParameters { get; set; } = new();
}

/// <summary>
/// Output containing executed extension results
/// </summary>
public sealed class GetExtensionsOutput
{
    /// <summary>
    /// Extension execution results as key-value pairs
    /// </summary>
    public Dictionary<string, object> Extensions { get; set; } = new();
}

