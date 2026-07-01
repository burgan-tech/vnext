using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Monitor.Functions.DTOs;

/// <summary>Input for listing domain-scoped function definitions.</summary>
public sealed class MonitorGetDomainFunctionsInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;
}

/// <summary>Input for listing functions registered in a specific instance's workflow.</summary>
public sealed class MonitorGetInstanceFunctionsInput : IHasDomain
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>The workflow (flow) key.</summary>
    [Required]
    public string Workflow { get; set; } = string.Empty;

    /// <summary>Instance key or ID.</summary>
    [Required]
    public string Instance { get; set; } = string.Empty;
}

/// <summary>A role grant entry on a function definition.</summary>
public sealed class MonitorFunctionRoleItem
{
    /// <summary>Role identifier (e.g. <c>morph-idm.maker</c>).</summary>
    public string? Role { get; set; }

    /// <summary>Grant type: <c>allow</c> or <c>deny</c>.</summary>
    public string? Grant { get; set; }
}

/// <summary>Summary of a function definition.</summary>
public sealed class MonitorFunctionItem
{
    /// <summary>Function definition key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Published version of the definition.</summary>
    public string? Version { get; set; }

    /// <summary>Scope of the function: <c>Domain</c>, <c>Flow</c>, or <c>Instance</c>.</summary>
    public string? Scope { get; set; }

    /// <summary>Number of tasks the function executes.</summary>
    public int TaskCount { get; set; }

    /// <summary>Role-based access rules defined on the function. Empty when no roles are configured.</summary>
    public List<MonitorFunctionRoleItem>? Roles { get; set; }
}

/// <summary>List of function definitions matching the requested scope.</summary>
public sealed class MonitorFunctionListResponse
{
    /// <summary>Matching function definitions.</summary>
    public List<MonitorFunctionItem> Items { get; set; } = [];

    /// <summary>Total number of matching functions.</summary>
    public int Total { get; set; }
}
