namespace BBT.Workflow.Monitor.Authorization.DTOs;

/// <summary>A role grant (role + allow/deny), mirrors orchestration RoleGrantDto.</summary>
public sealed class MonitorRoleGrant
{
    /// <summary>The role identifier (e.g. morph-idm.maker, domain.rolename).</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Grant type: "allow" or "deny". DENY always overrides ALLOW.</summary>
    public string Grant { get; set; } = string.Empty;
}

/// <summary>
/// Workflow authorization matrix. When <c>role</c> was supplied on the request, only entries
/// where the role appears are included; otherwise the full matrix is returned.
/// </summary>
public sealed class MonitorAuthorizationMatrixResponse
{
    /// <summary>The workflow definition key.</summary>
    public string? WorkflowKey { get; set; }

    /// <summary>The resolved workflow version.</summary>
    public string? Version { get; set; }

    /// <summary>Workflow-level query roles (who may read instance data).</summary>
    public List<MonitorRoleGrant> QueryRoles { get; set; } = [];

    /// <summary>Per-state view authorization entries.</summary>
    public List<MonitorStatePermission> States { get; set; } = [];

    /// <summary>Per-transition execution authorization entries.</summary>
    public List<MonitorTransitionPermission> Transitions { get; set; } = [];

    /// <summary>Per-function execution authorization entries.</summary>
    public List<MonitorFunctionPermission> Functions { get; set; } = [];
}

/// <summary>
/// Instance-scoped permissions view. Mirrors the workflow matrix shape but scoped to the
/// instance's current state: workflow-level query roles, the current state's entry, transitions
/// available from that state, and workflow functions.
/// </summary>
public sealed class MonitorInstancePermissionsResponse
{
    /// <summary>The workflow definition key resolved from the instance.</summary>
    public string? WorkflowKey { get; set; }

    /// <summary>The resolved workflow version.</summary>
    public string? Version { get; set; }

    /// <summary>Workflow-level query roles — same field as in the workflow matrix response.</summary>
    public List<MonitorRoleGrant> QueryRoles { get; set; } = [];

    /// <summary>
    /// The instance's current state with its own query roles.
    /// Uses the same <see cref="MonitorStatePermission"/> shape as the <c>states</c> array in the workflow matrix.
    /// </summary>
    public MonitorStatePermission? State { get; set; }

    /// <summary>Transitions available from the current state (state-scoped + applicable shared transitions).</summary>
    public List<MonitorTransitionPermission> Transitions { get; set; } = [];

    /// <summary>Workflow functions and their required roles.</summary>
    public List<MonitorFunctionPermission> Functions { get; set; } = [];
}

/// <summary>State-level view authorization.</summary>
public sealed class MonitorStatePermission
{
    /// <summary>The state key.</summary>
    public string? Key { get; set; }

    /// <summary>Roles permitted to view this state.</summary>
    public List<MonitorRoleGrant> QueryRoles { get; set; } = [];
}

/// <summary>Transition-level execution authorization.</summary>
public sealed class MonitorTransitionPermission
{
    /// <summary>The transition key.</summary>
    public string? Key { get; set; }

    /// <summary>The source state key.</summary>
    public string? From { get; set; }

    /// <summary>The target state key.</summary>
    public string? Target { get; set; }

    /// <summary>Roles permitted to execute this transition.</summary>
    public List<MonitorRoleGrant> Roles { get; set; } = [];
}

/// <summary>Function-level execution authorization.</summary>
public sealed class MonitorFunctionPermission
{
    /// <summary>The function key.</summary>
    public string? Key { get; set; }

    /// <summary>Roles permitted to invoke this function.</summary>
    public List<MonitorRoleGrant> Roles { get; set; } = [];
}

/// <summary>Transition permissions sub-view response.</summary>
public sealed class MonitorTransitionPermissionsResponse
{
    /// <summary>Transition-level permission entries.</summary>
    public List<MonitorTransitionPermission> Transitions { get; set; } = [];
}

/// <summary>Function permissions sub-view response.</summary>
public sealed class MonitorFunctionPermissionsResponse
{
    /// <summary>Function-level permission entries.</summary>
    public List<MonitorFunctionPermission> Functions { get; set; } = [];
}
