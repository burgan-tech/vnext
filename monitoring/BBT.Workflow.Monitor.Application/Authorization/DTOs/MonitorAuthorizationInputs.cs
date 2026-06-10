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

    /// <summary>Single role for authorization check; when provided together with <see cref="QueryRoles"/>, the response includes an <c>authorize</c> block.</summary>
    public string? Role { get; set; }

    /// <summary>Additional roles for authorization check.</summary>
    public List<string> QueryRoles { get; set; } = [];

    /// <summary>Transition key to scope the authorization check; if null, all transitions from the workflow are evaluated.</summary>
    public string? TransitionKey { get; set; }
}

/// <summary>Instance-scoped matrix query (P4 instance route) — resolves workflow from the instance. Optional role params enable inline authorization check.</summary>
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

    /// <summary>Single role for authorization check; when provided together with <see cref="QueryRoles"/>, the response includes an <c>authorize</c> block.</summary>
    public string? Role { get; set; }

    /// <summary>Additional roles for authorization check.</summary>
    public List<string> QueryRoles { get; set; } = [];

    /// <summary>Transition key to scope the authorization check; if null, all transitions from the current state are evaluated.</summary>
    public string? TransitionKey { get; set; }
}
