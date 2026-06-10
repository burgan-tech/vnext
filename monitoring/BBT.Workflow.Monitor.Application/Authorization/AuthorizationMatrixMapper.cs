using BBT.Workflow.Definitions;
using BBT.Workflow.Monitor.Authorization.DTOs;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Monitor.Authorization;

/// <summary>
/// Pure projection of a workflow definition into permission DTOs and DENY-overrides-ALLOW evaluation.
/// No I/O, no rule evaluation — read-only authorization view.
/// </summary>
public static class AuthorizationMatrixMapper
{
    /// <summary>Maps a collection of <see cref="RoleGrant"/> domain objects to DTOs.</summary>
    public static List<MonitorRoleGrant> Map(IEnumerable<RoleGrant> grants)
        => grants.Select(g => new MonitorRoleGrant { Role = g.Role, Grant = g.Grant }).ToList();

    /// <summary>Projects all transitions (state-scoped + shared) from a workflow into permission DTOs.</summary>
    public static List<MonitorTransitionPermission> MapTransitions(WorkflowDefinition flow)
        => flow.States.SelectMany(s => s.Transitions)
            .Concat(flow.SharedTransitions)
            .Select(t => new MonitorTransitionPermission
            {
                Key = t.Key,
                From = t.From,
                Target = t.Target,
                Roles = Map(t.Roles)
            })
            .ToList();

    /// <summary>Projects all states from a workflow into state-permission DTOs.</summary>
    public static List<MonitorStatePermission> MapStates(WorkflowDefinition flow)
        => flow.States
            .Select(s => new MonitorStatePermission
            {
                Key = s.Key,
                QueryRoles = Map(s.QueryRoles)
            })
            .ToList();

    /// <summary>
    /// Evaluates whether the supplied roles are granted access given a set of role grants.
    /// DENY overrides ALLOW; default deny when no grant matches the supplied roles.
    /// </summary>
    public static bool IsAllowed(IEnumerable<MonitorRoleGrant> grants, IEnumerable<string> roles)
    {
        var roleSet = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
        var matched = grants.Where(g => roleSet.Contains(g.Role)).ToList();
        if (matched.Count == 0) return false;
        if (matched.Any(g => string.Equals(g.Grant, "deny", StringComparison.OrdinalIgnoreCase))) return false;
        return matched.Any(g => string.Equals(g.Grant, "allow", StringComparison.OrdinalIgnoreCase));
    }
}
