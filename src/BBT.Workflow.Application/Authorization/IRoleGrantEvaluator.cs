using BBT.Workflow.Definitions;

namespace BBT.Workflow.Authorization;

/// <summary>
/// A batch-scoped role grant evaluator. Created once per authorization batch via
/// <see cref="ITransitionAuthorizationManager.CreateEvaluatorAsync"/> and then queried synchronously
/// any number of times, so that a caller evaluating many grant sets (schema field paths, the
/// transitions of a state, the instances of a list) pays the instance-bound I/O only once.
/// <para>
/// Semantics are identical to <see cref="ITransitionAuthorizationManager.IsRoleAllowedForGrantsAsync"/>:
/// DENY always wins; if at least one ALLOW grant exists an ALLOW match is required; a grant set with
/// no ALLOW grant is a blacklist (allowed unless explicitly denied); an empty grant set is allowed.
/// </para>
/// </summary>
public interface IRoleGrantEvaluator
{
    /// <summary>
    /// Evaluates a single caller role against a grant set.
    /// </summary>
    /// <param name="callerRole">
    /// The caller role to evaluate. Null is allowed: predefined and dynamic grants are still resolved,
    /// while static grants yield no match.
    /// </param>
    /// <param name="grants">The grant set to evaluate against. Empty → allowed.</param>
    /// <param name="transition">
    /// Optional transition supplying the <c>$.context.Transition.*</c> namespace for dynamic grants.
    /// Pass the transition whose <c>Roles</c> are being evaluated; pass null for grant sets that are
    /// not transition-scoped (function roles, queryRoles, schema <c>x-roles</c>).
    /// </param>
    bool IsRoleAllowed(
        string? callerRole,
        IReadOnlyCollection<RoleGrant> grants,
        Transition? transition = null);

    /// <summary>
    /// Evaluates all of the caller's roles against a grant set: any allowed role grants access.
    /// When <paramref name="callerRoles"/> is null or empty, a single null-role evaluation is performed
    /// so that predefined and dynamic grants still apply.
    /// </summary>
    bool IsAnyRoleAllowed(
        IReadOnlyCollection<string>? callerRoles,
        IReadOnlyCollection<RoleGrant> grants,
        Transition? transition = null);
}
