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

    /// <summary>
    /// Returns a copy of <paramref name="response"/> retaining only the entries where <paramref name="role"/> appears.
    /// Sections with no matching entries are returned as empty lists; <c>state</c> becomes <c>null</c> when it has no match.
    /// </summary>
    public static MonitorInstancePermissionsResponse FilterByRole(
        MonitorInstancePermissionsResponse response, string role)
    {
        var filteredState = response.State is { } s
            ? new MonitorStatePermission
            {
                Key = s.Key,
                QueryRoles = s.QueryRoles
                    .Where(g => string.Equals(g.Role, role, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            }
            : null;

        return new MonitorInstancePermissionsResponse
        {
            WorkflowKey = response.WorkflowKey,
            Version = response.Version,
            QueryRoles = response.QueryRoles
                .Where(g => string.Equals(g.Role, role, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            State = filteredState?.QueryRoles.Count > 0 ? filteredState : null,
            Transitions = response.Transitions
                .Select(t => new MonitorTransitionPermission
                {
                    Key = t.Key,
                    From = t.From,
                    Target = t.Target,
                    Roles = t.Roles
                        .Where(g => string.Equals(g.Role, role, StringComparison.OrdinalIgnoreCase))
                        .ToList()
                })
                .Where(t => t.Roles.Count > 0)
                .ToList(),
            Functions = response.Functions
                .Select(f => new MonitorFunctionPermission
                {
                    Key = f.Key,
                    Roles = f.Roles
                        .Where(g => string.Equals(g.Role, role, StringComparison.OrdinalIgnoreCase))
                        .ToList()
                })
                .Where(f => f.Roles.Count > 0)
                .ToList()
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="matrix"/> retaining only the entries where <paramref name="role"/> appears.
    /// Sections with no matching entries are returned as empty lists.
    /// </summary>
    public static MonitorAuthorizationMatrixResponse FilterByRole(
        MonitorAuthorizationMatrixResponse matrix, string role)
    {
        return new MonitorAuthorizationMatrixResponse
        {
            WorkflowKey = matrix.WorkflowKey,
            Version = matrix.Version,
            QueryRoles = matrix.QueryRoles
                .Where(g => string.Equals(g.Role, role, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            States = matrix.States
                .Select(s => new MonitorStatePermission
                {
                    Key = s.Key,
                    QueryRoles = s.QueryRoles
                        .Where(g => string.Equals(g.Role, role, StringComparison.OrdinalIgnoreCase))
                        .ToList()
                })
                .Where(s => s.QueryRoles.Count > 0)
                .ToList(),
            Transitions = matrix.Transitions
                .Select(t => new MonitorTransitionPermission
                {
                    Key = t.Key,
                    From = t.From,
                    Target = t.Target,
                    Roles = t.Roles
                        .Where(g => string.Equals(g.Role, role, StringComparison.OrdinalIgnoreCase))
                        .ToList()
                })
                .Where(t => t.Roles.Count > 0)
                .ToList(),
            Functions = matrix.Functions
                .Select(f => new MonitorFunctionPermission
                {
                    Key = f.Key,
                    Roles = f.Roles
                        .Where(g => string.Equals(g.Role, role, StringComparison.OrdinalIgnoreCase))
                        .ToList()
                })
                .Where(f => f.Roles.Count > 0)
                .ToList()
        };
    }
}
