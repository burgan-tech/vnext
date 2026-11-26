using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Decides which master-schema paths a caller may not name in a SET query — the instance list's filter, sort,
/// groupBy and aggregations (vnext#1262). Those run in SQL on the stored value and answer for many rows at once,
/// so the per-instance exposure pass (<see cref="SchemaFieldFilterService"/>) never sees them: a group key or a
/// <c>min</c>/<c>max</c> returns the value outright, and a filter or a sort order is an oracle on it.
/// </summary>
/// <remarks>
/// <para>The result feeds <see cref="SchemaFilterContext.RestrictedPaths"/>; the SQL builders refuse a
/// caller-supplied field that touches one of the paths (400), the same way they refuse an encrypted path.</para>
/// <para>A path is restricted when</para>
/// <list type="bullet">
/// <item>it carries <c>x-roles</c> and the grant set does not admit the caller for EVERY row
/// (<see cref="TransitionAuthorizationManager.EvaluateRolesForRowSet"/>): a grant whose answer depends on the
/// instance — <c>$InstanceStarter</c>, <c>$user.…</c>, <c>$role.…</c> — cannot be decided for a set and
/// restricts the path, even though the plain list may show the field on some rows;</item>
/// <item>it carries <c>x-masking</c>, masking is enabled, and the caller matches no exemption grant — the
/// column holds the clear value the caller only ever reads masked.</item>
/// </list>
/// <para><c>x-encryption</c> paths are not listed: an <c>encrypt</c> path is refused for every caller through
/// <see cref="SchemaFilterContext.EncryptedPaths"/>, and a <c>hash</c> path stores the digest every reader gets.</para>
/// </remarks>
public static class SchemaQueryRestrictedPaths
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(0);

    /// <summary>
    /// True when <paramref name="exposure"/> declares a path that could be restricted for some caller, so the
    /// caller's roles are worth resolving.
    /// </summary>
    /// <param name="exposure">The master schema's exposure metadata.</param>
    /// <param name="maskingEnabled"><c>SchemaMasking:Enabled</c>.</param>
    public static bool HasGuardedPaths(SchemaExposureMetadata exposure, bool maskingEnabled)
    {
        ArgumentNullException.ThrowIfNull(exposure);
        if (exposure.PathRoleGrants.Count > 0)
            return true;
        if (!maskingEnabled)
            return false;

        foreach (var rule in exposure.PathMaskRules.Values)
        {
            if (!rule.IsEncryption)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The paths of <paramref name="exposure"/> the caller may not filter, sort, group or aggregate on.
    /// </summary>
    /// <param name="exposure">The master schema's exposure metadata.</param>
    /// <param name="callerRoles">The caller's whole role set; ignored when <paramref name="rolesResolved"/> is false.</param>
    /// <param name="rolesResolved">
    /// False when the role provider failed: every guarded and every masked path is restricted (fail closed),
    /// the direction the exposure pass takes when it prunes every guarded field.
    /// </param>
    /// <param name="maskingEnabled"><c>SchemaMasking:Enabled</c>; when false <c>x-masking</c> paths are served
    /// clear to everyone and are not restricted.</param>
    /// <returns>The restricted paths (ordinal); empty when the caller may query every path.</returns>
    public static IReadOnlySet<string> Resolve(
        SchemaExposureMetadata exposure,
        IReadOnlyCollection<string>? callerRoles,
        bool rolesResolved,
        bool maskingEnabled)
    {
        ArgumentNullException.ThrowIfNull(exposure);

        HashSet<string>? restricted = null;
        foreach (var (path, grants) in exposure.PathRoleGrants)
        {
            if (!rolesResolved || !TransitionAuthorizationManager.EvaluateRolesForRowSet(callerRoles, grants))
                (restricted ??= new HashSet<string>(StringComparer.Ordinal)).Add(path);
        }

        if (maskingEnabled)
        {
            foreach (var (path, rule) in exposure.PathMaskRules)
            {
                if (rule.IsEncryption)
                    continue;

                // Allow-only exemption list: an empty one exempts nobody, and only a proven match exempts.
                var exempt = rolesResolved && rule.ExemptRoles.Count > 0 &&
                    TransitionAuthorizationManager.EvaluateRolesForRowSet(
                        callerRoles, rule.ExemptRoles, defaultAllowWhenNoAllowGrant: false);
                if (!exempt)
                    (restricted ??= new HashSet<string>(StringComparer.Ordinal)).Add(path);
            }
        }

        return restricted ?? None;
    }
}
