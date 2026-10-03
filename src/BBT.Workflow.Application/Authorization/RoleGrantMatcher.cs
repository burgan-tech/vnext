using BBT.Workflow.Definitions;

namespace BBT.Workflow.Authorization;

/// <summary>
/// The one set-level decision rule for role grants, shared by the instance-bound
/// <see cref="RoleGrantEvaluator"/> and its no-instance twin
/// <see cref="TransitionAuthorizationManager.EvaluateRolesStatic(IReadOnlyCollection{string}, IReadOnlyCollection{RoleGrant}, bool)"/>.
/// The two differ only in how a LEAF is matched (the <c>leaf</c> callback); how leaves compose into a
/// grant and grants into a decision lives here and nowhere else, so the paths cannot drift.
/// </summary>
/// <remarks>
/// <para><b>Grant level (Kleene).</b> <c>allOf</c>: a <see cref="GrantMatch.No"/> leaf dominates, else
/// any <see cref="GrantMatch.Unknown"/> leaf makes it Unknown, else Yes. <c>anyOf</c>: a Yes leaf
/// dominates, else Unknown if any, else No. A plain <c>role</c> grant is its single leaf.</para>
/// <para><b>Set level.</b> The DENY group is an AND evaluated first: a deny that cannot be ruled out —
/// Yes OR Unknown — refuses. The ALLOW group is an OR: only a proven Yes admits. Empty set → allow; a
/// set with no ALLOW grant (a blacklist) admits whatever no deny refused, unless the caller asks for
/// strict allowlist semantics.</para>
/// </remarks>
internal static class RoleGrantMatcher
{
    /// <summary>Evaluates one grant — a single role or an <c>allOf</c> / <c>anyOf</c> combinator.</summary>
    public static GrantMatch Evaluate(RoleGrant grant, Func<string, GrantMatch> leaf)
    {
        if (grant.Role is not null)
            return leaf(grant.Role);

        if (grant.AllOf is { } all)
        {
            var result = GrantMatch.Yes;
            foreach (var condition in all)
            {
                var match = leaf(condition.Role);
                if (match == GrantMatch.No)
                    return GrantMatch.No; // AND: No dominates
                if (match == GrantMatch.Unknown)
                    result = GrantMatch.Unknown;
            }

            return result;
        }

        var any = GrantMatch.No;
        foreach (var condition in grant.AnyOf!)
        {
            var match = leaf(condition.Role);
            if (match == GrantMatch.Yes)
                return GrantMatch.Yes; // OR: Yes dominates
            if (match == GrantMatch.Unknown)
                any = GrantMatch.Unknown;
        }

        return any;
    }

    /// <summary>Decides a whole grant set: DENY group (AND, first), then ALLOW group (OR).</summary>
    /// <param name="grants">The grant set.</param>
    /// <param name="leaf">Matches one leaf role string against the caller.</param>
    /// <param name="defaultAllowWhenNoAllowGrant">
    /// When true (default), a set with no ALLOW grant admits a caller no deny refused.
    /// </param>
    public static bool Decide(
        IReadOnlyCollection<RoleGrant> grants,
        Func<string, GrantMatch> leaf,
        bool defaultAllowWhenNoAllowGrant = true)
    {
        if (grants.Count == 0)
            return true;

        // DENY group, AND — a deny that cannot be ruled out refuses (Yes or Unknown).
        foreach (var grant in grants)
        {
            if (grant.IsDeny && Evaluate(grant, leaf) != GrantMatch.No)
                return false;
        }

        // ALLOW group, OR — only a proven match admits.
        var hasAllowGrant = false;
        foreach (var grant in grants)
        {
            if (!grant.IsAllow)
                continue;

            hasAllowGrant = true;
            if (Evaluate(grant, leaf) == GrantMatch.Yes)
                return true;
        }

        return defaultAllowWhenNoAllowGrant && !hasAllowGrant;
    }
}
