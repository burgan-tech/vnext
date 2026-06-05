using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances.DTOs;

/// <summary>
/// Input for retrieving instance schema
/// </summary>
public sealed class GetSchemaInput: IHasDomain
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
}

public sealed class GetSchemaOutput
{
    /// <summary>
    /// The schema key
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// The schema type
    /// </summary>
    public string Type { get; set; }

    public JsonElement Schema { get; set; }
}

