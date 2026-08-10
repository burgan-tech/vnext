using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Evaluates role grants (static + predefined + dynamic context references).
/// DENY always wins. If at least one ALLOW grant exists, the set is an allowlist (default deny unless an ALLOW matches).
/// A grant set with no ALLOW grant is a blacklist (default allow unless a matching DENY applies).
/// <para>
/// Predefined actor roles ($InstanceStarter, $PreviousUser) are matched against <c>ICurrentUser.ActorUserName</c>.
/// Predefined behalf-of roles ($InstanceBehalfOfStarter, $PreviousBehalfOfUser) are matched against <c>ICurrentUser.UserName</c>.
/// Dynamic roles ($user, $userBehalfOf, $role) resolve values from the authorization context via a ScriptContext-compatible path.
/// </para>
/// <para>
/// All instance-bound evaluation funnels through a single <see cref="IRoleGrantEvaluator"/>; the methods on
/// this class are thin wrappers that build one evaluator and query it. Callers that evaluate many grant sets
/// should create the evaluator themselves via <see cref="CreateEvaluatorAsync"/> so the instance-bound I/O is
/// paid once for the whole batch.
/// </para>
/// </summary>
public sealed class TransitionAuthorizationManager(
    ICurrentUser currentUser,
    IInstanceTransitionRepository instanceTransitionRepository) : ITransitionAuthorizationManager
{
    /// <inheritdoc />
    public async Task<IRoleGrantEvaluator> CreateEvaluatorAsync(
        Instance? instance,
        WorkflowDefinition? workflow,
        AuthorizationRequestContext? requestContext,
        IEnumerable<RoleGrant> grantsForPrefetchHint,
        CancellationToken cancellationToken = default)
    {
        // Without an instance there is nothing for predefined or dynamic grants to resolve against,
        // so the evaluator degrades to the static comparison and needs no prefetch.
        if (instance == null)
            return new RoleGrantEvaluator(null, null, null, null, null, null);

        // Fetch the previous manual transition only when some grant in the batch actually references it.
        InstanceTransition? previousTransition = null;
        if (grantsForPrefetchHint.Any(g => ReferencesPreviousTransition(g.Role)))
        {
            previousTransition = await instanceTransitionRepository
                .GetLastCompletedManualTransitionAsync(instance.Id, cancellationToken);
        }

        return new RoleGrantEvaluator(
            instance,
            workflow,
            requestContext,
            previousTransition,
            currentUser.ActorUserName?.Trim(),
            currentUser.UserName?.Trim());
    }

    /// <inheritdoc />
    public async Task<bool> IsTransitionAllowedForRoleAsync(
        WorkflowDefinition workflow,
        Transition transition,
        Instance? instance,
        string? role,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        var roleGrants = transition.Roles;
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow

        var evaluator = await CreateEvaluatorAsync(
            instance, workflow, requestContext, roleGrants, cancellationToken);

        return evaluator.IsRoleAllowed(role, roleGrants, transition);
    }

    /// <inheritdoc />
    public async Task<bool> IsTransitionAllowedInStateAsync(
        WorkflowDefinition workflow,
        Transition transition,
        string? currentStateKey,
        Instance? instance,
        string? role,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        // Without a current state there is no availableIn entry to resolve, so this degrades to the
        // transition-level grant check (workflow-scoped authorize keeps its previous behaviour).
        if (string.IsNullOrEmpty(currentStateKey))
            return await IsTransitionAllowedForRoleAsync(
                workflow, transition, instance, role, requestContext, cancellationToken);

        // State gate first: a transition not offered in this state is denied without evaluating roles.
        if (!transition.IsAvailableInState(currentStateKey))
            return false;

        var stateEntry = transition.FindAvailableIn(currentStateKey);

        if (transition.Roles.Count == 0 && stateEntry is not { HasRoles: true })
            return true; // No grants on either level → allow

        var evaluator = await CreateEvaluatorAsync(
            instance,
            workflow,
            requestContext,
            transition.Roles.Concat(stateEntry?.Roles ?? []),
            cancellationToken);

        return IsAllowedWithStateNarrowing(evaluator, role, transition, stateEntry);
    }

    /// <inheritdoc />
    public async Task<bool> IsAnyRoleAllowedInStateAsync(
        WorkflowDefinition workflow,
        Transition transition,
        string? currentStateKey,
        Instance? instance,
        IReadOnlyCollection<string>? callerRoles,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(currentStateKey))
        {
            var evaluator = await CreateEvaluatorAsync(
                instance, workflow, requestContext, transition.Roles, cancellationToken);
            return evaluator.IsAnyRoleAllowed(callerRoles, transition.Roles, transition);
        }

        if (!transition.IsAvailableInState(currentStateKey))
            return false;

        var stateEntry = transition.FindAvailableIn(currentStateKey);
        var evaluatorForState = await CreateEvaluatorAsync(
            instance,
            workflow,
            requestContext,
            transition.Roles.Concat(stateEntry?.Roles ?? []),
            cancellationToken);

        return evaluatorForState.IsAnyRoleAllowed(callerRoles, transition.Roles, transition)
               && (stateEntry is not { HasRoles: true }
                   || evaluatorForState.IsAnyRoleAllowed(callerRoles, stateEntry.Roles, transition));
    }

    /// <summary>
    /// Applies the canonical composition of the two grant levels: the transition's own grants are the
    /// global gate and a matching <c>availableIn</c> entry's grants are an additional, state-specific
    /// narrowing — <b>both</b> must allow (AND).
    /// <para>
    /// Each level is evaluated by the shared <see cref="IRoleGrantEvaluator"/>, so DENY-wins,
    /// allowlist/blacklist and predefined/dynamic resolution behave identically at both levels. An
    /// empty grant set is allowed, which is what makes a role-less entry — and therefore the legacy
    /// bare-string <c>availableIn</c> form — behave exactly as before.
    /// </para>
    /// </summary>
    private static bool IsAllowedWithStateNarrowing(
        IRoleGrantEvaluator evaluator,
        string? role,
        Transition transition,
        AvailableInEntry? stateEntry)
    {
        if (!evaluator.IsRoleAllowed(role, transition.Roles, transition))
            return false;

        return stateEntry is not { HasRoles: true }
               || evaluator.IsRoleAllowed(role, stateEntry.Roles, transition);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FilterAuthorizedTransitionKeysAsync(
        WorkflowDefinition workflow,
        State currentState,
        Instance? instance,
        IReadOnlyList<string> transitionKeys,
        string? role,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (transitionKeys.Count == 0)
            return transitionKeys;

        // Resolve first so the prefetch hint covers every grant this batch will evaluate.
        // AvailableInEntry is captured alongside the transition because a per-state grant set narrows
        // the transition-level one, and its grants must be in the hint too — a $PreviousUser grant
        // missing from the hint can never match.
        var candidates = new List<(string Key, Transition Transition, AvailableInEntry? StateEntry)>(transitionKeys.Count);
        foreach (var key in transitionKeys)
        {
            var transition = workflow.FindTransitionInContext(key);
            if (transition != null)
                candidates.Add((key, transition, transition.FindAvailableIn(currentState.Key)));
        }

        if (candidates.Count == 0)
            return [];

        var evaluator = await CreateEvaluatorAsync(
            instance,
            workflow,
            requestContext,
            candidates.SelectMany(c => c.Transition.Roles.Concat(c.StateEntry?.Roles ?? [])),
            cancellationToken);

        var result = new List<string>(candidates.Count);
        foreach (var (key, transition, stateEntry) in candidates)
        {
            if (IsAllowedWithStateNarrowing(evaluator, role, transition, stateEntry))
                result.Add(key);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> IsRoleAllowedForGrantsAsync(
        string? role,
        IReadOnlyCollection<RoleGrant> roleGrants,
        Instance? instance,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow

        var evaluator = await CreateEvaluatorAsync(
            instance, workflow: null, requestContext, roleGrants, cancellationToken);

        return evaluator.IsRoleAllowed(role, roleGrants);
    }

    /// <inheritdoc />
    public async Task<bool> IsAnyRoleAllowedForGrantsAsync(
        IReadOnlyCollection<string>? callerRoles,
        IReadOnlyCollection<RoleGrant> roleGrants,
        Instance? instance,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow

        var evaluator = await CreateEvaluatorAsync(
            instance, workflow: null, requestContext, roleGrants, cancellationToken);

        return evaluator.IsAnyRoleAllowed(callerRoles, roleGrants);
    }

    /// <inheritdoc />
    public async Task<bool> IsQueryAllowedAsync(
        WorkflowDefinition workflow,
        Instance instance,
        IReadOnlyCollection<string>? callerRoles,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        var currentStateKey = instance.GetEffectiveState;
        var state = string.IsNullOrWhiteSpace(currentStateKey) ? null : workflow.FindState(currentStateKey);
        var queryRoles = state is { QueryRoles.Count: > 0 } ? state.QueryRoles : workflow.QueryRoles;

        return await IsAnyRoleAllowedForGrantsAsync(callerRoles, queryRoles, instance, requestContext, cancellationToken);
    }

    /// <summary>
    /// True when the grant role references the previous manual transition and therefore requires
    /// the transition prefetch.
    /// </summary>
    private static bool ReferencesPreviousTransition(string? grantRole) =>
        string.Equals(grantRole, PredefinedInstanceRoles.PreviousUser, StringComparison.Ordinal) ||
        string.Equals(grantRole, PredefinedInstanceRoles.PreviousBehalfOfUser, StringComparison.Ordinal);

    /// <summary>
    /// Evaluates role against role grants (static only). DENY always wins.
    /// If at least one ALLOW grant exists, the set is an allowlist (default deny unless an ALLOW matches).
    /// A grant set with no ALLOW grant is a blacklist (default allow unless a matching DENY applies),
    /// controlled by <paramref name="defaultAllowWhenNoAllowGrant"/>.
    /// When role is null, no regular role grants match; only the grant count check applies (empty grants → allow).
    /// Used as the no-instance path of <see cref="IRoleGrantEvaluator"/>, where predefined and dynamic
    /// grants have nothing to resolve against.
    /// </summary>
    /// <param name="role">The caller role to evaluate, or null.</param>
    /// <param name="roleGrants">The grant set to evaluate against.</param>
    /// <param name="defaultAllowWhenNoAllowGrant">
    /// When true (default), a grant set with no ALLOW grant allows callers that are not explicitly denied.
    /// Set to false to force strict allowlist semantics.
    /// </param>
    public static bool EvaluateRolesStatic(string? role, IReadOnlyCollection<RoleGrant> roleGrants,
        bool defaultAllowWhenNoAllowGrant = true)
    {
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow
        var normalizedRole = role?.Trim() ?? string.Empty;
        foreach (var g in roleGrants)
        {
            if (string.Equals(g.Role, normalizedRole, StringComparison.OrdinalIgnoreCase) && g.IsDeny)
                return false;
        }
        foreach (var g in roleGrants)
        {
            if (string.Equals(g.Role, normalizedRole, StringComparison.OrdinalIgnoreCase) && g.IsAllow)
                return true;
        }
        // Blacklist (deny-only) set: no ALLOW grant defined → allow when not explicitly denied.
        if (defaultAllowWhenNoAllowGrant && !roleGrants.Any(g => g.IsAllow))
            return true;
        return false;
    }
}
