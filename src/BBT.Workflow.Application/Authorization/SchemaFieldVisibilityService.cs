using System.Collections.Generic;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Computes which schema property paths are visible to the caller based on role grants.
/// Used for master schema field-level visibility: paths with no "roles" in schema are visible to all;
/// paths with "roles" are visible only if at least one caller role is allowed (DENY wins over ALLOW).
/// </summary>
public static class SchemaFieldVisibilityService
{
    /// <summary>
    /// Gets the set of property paths that the caller is allowed to see.
    /// </summary>
    /// <param name="pathRoleGrants">Map of property path to role grants (from SchemaRolesParser).</param>
    /// <param name="callerRoles">Caller roles (e.g. from ICurrentUser.Roles).</param>
    /// <returns>Set of visible property paths. Paths not in pathRoleGrants are considered visible to all and are not included; only paths that have roles and are allowed are included. For filtering, include all paths that exist in the data: paths without roles in schema → visible; paths with roles → include if allowed.</returns>
    public static IReadOnlySet<string> GetVisiblePaths(
        IReadOnlyDictionary<string, IReadOnlyList<RoleGrant>> pathRoleGrants,
        IReadOnlyList<string>? callerRoles)
    {
        if (pathRoleGrants.Count == 0)
            return new HashSet<string>(0);

        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, grants) in pathRoleGrants)
        {
            if (IsPathVisibleForCaller(grants, callerRoles))
                visible.Add(path);
        }
        return visible;
    }

    /// <summary>
    /// Determines whether a property with the given role grants is visible to the caller.
    /// Semantics: DENY wins; if no DENY match for any caller role, then any ALLOW match for any caller role → visible.
    /// </summary>
    public static bool IsPathVisibleForCaller(
        IReadOnlyList<RoleGrant> roleGrants,
        IReadOnlyList<string>? callerRoles)
    {
        if (roleGrants.Count == 0)
            return true;

        if (callerRoles is null || callerRoles.Count == 0)
            return false;

        foreach (var role in callerRoles)
        {
            if (string.IsNullOrWhiteSpace(role))
                continue;
            if (TransitionAuthorizationManager.EvaluateRolesStatic(role.Trim(), roleGrants))
                return true;
        }
        return false;
    }
}
