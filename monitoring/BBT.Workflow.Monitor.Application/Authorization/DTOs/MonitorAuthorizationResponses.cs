namespace BBT.Workflow.Monitor.Authorization.DTOs;

/// <summary>A role grant (role + allow/deny), mirrors orchestration RoleGrantDto.</summary>
public sealed class MonitorRoleGrant
{
    /// <summary>The role identifier (e.g. morph-idm.maker, domain.rolename).</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Grant type: "allow" or "deny". DENY always overrides ALLOW.</summary>
    public string Grant { get; set; } = string.Empty;
}

/// <summary>Full workflow authorization matrix (P4). When role parameters are supplied, also includes an inline authorization verdict.</summary>
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

    /// <summary>Inline authorization verdict. Present only when <c>role</c> or <c>queryRoles</c> parameters are supplied.</summary>
    public MonitorAuthorizeResult? Authorize { get; set; }
}

/// <summary>State-level view authorization.</summary>
public sealed class MonitorStatePermission
{
    /// <summary>The state key.</summary>
    public string? Key { get; set; }

    /// <summary>Roles permitted to view this state.</summary>
    public List<MonitorRoleGrant> QueryRoles { get; set; } = [];
}

/// <summary>Transition-level execution authorization (P17 item).</summary>
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

/// <summary>Function-level execution authorization (P19 item).</summary>
public sealed class MonitorFunctionPermission
{
    /// <summary>The function key.</summary>
    public string? Key { get; set; }

    /// <summary>Roles permitted to invoke this function.</summary>
    public List<MonitorRoleGrant> Roles { get; set; } = [];
}

/// <summary>P17 sub-view response — transition permissions only.</summary>
public sealed class MonitorTransitionPermissionsResponse
{
    /// <summary>Transition-level permission entries.</summary>
    public List<MonitorTransitionPermission> Transitions { get; set; } = [];
}

/// <summary>P19 sub-view response — function permissions only.</summary>
public sealed class MonitorFunctionPermissionsResponse
{
    /// <summary>Function-level permission entries.</summary>
    public List<MonitorFunctionPermission> Functions { get; set; } = [];
}

/// <summary>Inline authorization verdict embedded in the permissions matrix response.</summary>
public sealed class MonitorAuthorizeResult
{
    /// <summary>True when at least one matching transition grants access and no deny overrides it.</summary>
    public bool Allowed { get; set; }

    /// <summary>The role identifiers that matched transition grants (allow grants only).</summary>
    public List<string> MatchedRoles { get; set; } = [];
}
