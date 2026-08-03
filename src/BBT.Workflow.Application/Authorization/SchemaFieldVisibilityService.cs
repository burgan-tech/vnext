using BBT.Workflow.Definitions;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Computes which schema property paths are visible to the caller based on <c>x-roles</c> grants.
/// Paths with no grants in the schema are visible to all; paths with grants are visible only when the
/// caller resolves to an allow.
/// <para>
/// Evaluation is delegated to a shared <see cref="IRoleGrantEvaluator"/>, so field-level visibility
/// honors exactly the same grant forms as every other authorization surface — static roles, predefined
/// instance roles, and dynamic <c>$.context.*</c> references — and DENY wins over the whole grant set
/// rather than per caller role.
/// </para>
/// </summary>
public static class SchemaFieldVisibilityService
{
    /// <summary>
    /// Gets the set of property paths that the caller is allowed to see.
    /// </summary>
    /// <param name="pathRoleGrants">Map of property path to role grants (from SchemaRolesParser).</param>
    /// <param name="callerRoles">Caller roles.</param>
    /// <param name="evaluator">
    /// Evaluator built for the instance being read. Create it once for the whole schema — via
    /// <see cref="ITransitionAuthorizationManager.CreateEvaluatorAsync"/> with the union of every path's
    /// grants — so a schema with many guarded fields costs one prefetch, not one per field.
    /// </param>
    /// <returns>
    /// Set of visible property paths. Paths not present in <paramref name="pathRoleGrants"/> carry no grants
    /// and are visible to all, so they are not listed here; only guarded paths that resolve to an allow are.
    /// </returns>
    public static IReadOnlySet<string> GetVisiblePaths(
        IReadOnlyDictionary<string, IReadOnlyList<RoleGrant>> pathRoleGrants,
        IReadOnlyList<string>? callerRoles,
        IRoleGrantEvaluator evaluator)
    {
        if (pathRoleGrants.Count == 0)
            return new HashSet<string>(0);

        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, grants) in pathRoleGrants)
        {
            if (IsPathVisibleForCaller(grants, callerRoles, evaluator))
                visible.Add(path);
        }
        return visible;
    }

    /// <summary>
    /// Determines whether a property with the given role grants is visible to the caller.
    /// Semantics are the canonical grant rule: DENY wins; if at least one ALLOW grant exists an ALLOW match
    /// is required; a grant set with no ALLOW grant is a blacklist (visible unless explicitly denied);
    /// an empty grant set is visible.
    /// </summary>
    public static bool IsPathVisibleForCaller(
        IReadOnlyList<RoleGrant> roleGrants,
        IReadOnlyList<string>? callerRoles,
        IRoleGrantEvaluator evaluator)
    {
        if (roleGrants.Count == 0)
            return true;

        return evaluator.IsAnyRoleAllowed(callerRoles, roleGrants);
    }
}
