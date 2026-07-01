using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Monitor.Authorization.DTOs;

/// <summary>Workflow-scoped permission/matrix query (P4 workflow route, P17, P19).</summary>
public sealed class MonitorGetWorkflowPermissionsInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Optional version filter; if null, returns the latest version.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// When provided, the response is filtered to only include entries where this role appears.
    /// When omitted, the full permission matrix is returned.
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>Instance-scoped permissions query — resolves workflow and current state from the instance.</summary>
public sealed class MonitorGetInstancePermissionsInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>The instance identifier.</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;

    /// <summary>
    /// Optional flow version override. When provided, the permissions are resolved against this specific
    /// version of the flow definition instead of the version the instance is running on.
    /// When omitted, the instance's own flow version is used.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// When provided, the response is filtered to only include entries where this role appears.
    /// When omitted, all role entries for the current state are returned.
    /// </summary>
    public string? Role { get; set; }
}
