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
            // Spanned INSIDE the branch, so the span exists only when the query ran. A span on the
            // other side of this guard would report a lookup that never happened, and "this trace
            // has no PreviousUserLookup" would stop meaning "no extra query was needed" — the same
            // rule the pipeline follows when it drops a step that did no work.
            using var lookup = AuthorizationActivityHelper.StartPreviousUserLookup();

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
        IReadOnlyCollection<string>? callerRoles,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        // The parent's stamped override wins outright, and it REPLACES: a parent that narrowed this
        // transition meant to narrow it, so the child's own grants do not also get a say. Absent an
        // override, the transition's own definition applies. Resolved here rather than per surface —
        // see EffectiveTransitionGrants.
        var roleGrants = EffectiveTransitionGrants(instance, transition);
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow

        var evaluator = await CreateEvaluatorAsync(
            instance, workflow, requestContext, roleGrants, cancellationToken);

        return evaluator.IsAnyRoleAllowed(callerRoles, roleGrants, transition);
    }

    /// <summary>
    /// The grants that actually decide a transition for this instance: the parent's stamped override
    /// when there is one, otherwise the transition's own <c>roles</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Override wins, and it replaces.</b> There is no merge: a parent narrowing a child's
    /// transition meant to narrow it, and OR-ing the child's own grants back in would hand the
    /// narrowing straight back to the roles it was taken from.
    /// </para>
    /// <para>
    /// Resolved in ONE place because resolving it per surface is how the surfaces diverged. The
    /// state function read the map stamped on the child and honoured the narrowing; <c>authorize</c>
    /// read <c>subFlowConfig.Overrides</c> off the PARENT's definition, which a directly-addressed
    /// leaf does not have, and answered from the child's own grants — measured at such a leaf, the
    /// two gave opposite answers for both roles.
    /// </para>
    /// <para>
    /// No <c>availableIn</c> narrowing is applied on the override branch: these overrides key off the
    /// SUBFLOW's transitions, so the parent's <c>availableIn</c> states do not describe them.
    /// </para>
    /// </remarks>
    private static IReadOnlyCollection<RoleGrant> EffectiveTransitionGrants(
        Instance? instance,
        Transition transition)
    {
        if (instance is null)
            return transition.Roles;

        return SubFlowTransitionOverrideReader.TryReadRoles(instance, transition.Key)
               ?? transition.Roles;
    }

    /// <inheritdoc />
    public async Task<bool> IsTransitionAllowedInStateAsync(
        WorkflowDefinition workflow,
        Transition transition,
        string? currentStateKey,
        Instance? instance,
        IReadOnlyCollection<string>? callerRoles,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        // Without a current state there is no availableIn entry to resolve, so this degrades to the
        // transition-level grant check (workflow-scoped authorize keeps its previous behaviour).
        if (string.IsNullOrEmpty(currentStateKey))
            return await IsTransitionAllowedForRoleAsync(
                workflow, transition, instance, callerRoles, requestContext, cancellationToken);

        // State gate first: a transition not offered in this state is denied without evaluating roles.
        //
        // `availableIn` is the gate for SHARED and well-known transitions, and its "empty means every
        // state" rule is correct for them. It is wrong for a STATE transition, which carries no
        // availableIn at all and is implicitly scoped to the state that declares it — read through
        // IsAvailableInState, an empty list made `approve` (declared only on `review`) answer
        // "allowed" for an instance sitting in `intake`. Measured on role-matrix-lab: the state
        // function offered [submit-for-review, record-note, cancel-role-matrix] while
        // authorize?transitionKey=approve answered 200. The execution policy then rejects the call
        // with Transition:100021, so the oracle was wrong in the PERMISSIVE direction — which is the
        // direction that matters once a middle tier admits on its answer.
        //
        // An earlier fix made this branch state-aware for transitions that HAVE an availableIn; this
        // closes the other half.
        if (IsStateScoped(workflow, transition.Key))
        {
            var declaredHere = workflow.FindState(currentStateKey)?.Transitions
                .Any(t => string.Equals(t.Key, transition.Key, StringComparison.Ordinal)) == true;

            if (!declaredHere)
                return false;
        }
        else if (!transition.IsAvailableInState(currentStateKey))
        {
            return false;
        }

        var stateEntry = transition.FindAvailableIn(currentStateKey);

        // A stamped override replaces the transition's own grants AND skips availableIn narrowing:
        // the override describes the SUBFLOW's transition, which the parent's availableIn states do
        // not speak about.
        var stamped = instance is null
            ? null
            : SubFlowTransitionOverrideReader.TryReadRoles(instance, transition.Key);

        if (stamped is { Count: > 0 })
        {
            var overrideEvaluator = await CreateEvaluatorAsync(
                instance, workflow, requestContext, stamped, cancellationToken);
            return overrideEvaluator.IsAnyRoleAllowed(callerRoles, stamped, transition);
        }

        if (transition.Roles.Count == 0 && stateEntry is not { HasRoles: true })
            return true; // No grants on either level → allow

        var evaluator = await CreateEvaluatorAsync(
            instance,
            workflow,
            requestContext,
            transition.Roles.Concat(stateEntry?.Roles ?? []),
            cancellationToken);

        return IsAllowedWithStateNarrowing(evaluator, callerRoles, transition, stateEntry);
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
        IReadOnlyCollection<string>? callerRoles,
        Transition transition,
        AvailableInEntry? stateEntry)
    {
        if (!evaluator.IsAnyRoleAllowed(callerRoles, transition.Roles, transition))
            return false;

        return stateEntry is not { HasRoles: true }
               || evaluator.IsAnyRoleAllowed(callerRoles, stateEntry.Roles, transition);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FilterAuthorizedTransitionKeysAsync(
        WorkflowDefinition workflow,
        State currentState,
        Instance? instance,
        IReadOnlyList<string> transitionKeys,
        IReadOnlyCollection<string>? callerRoles,
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

        // The prefetch hint must cover the grants that will actually be evaluated, which for an
        // overridden transition is the parent's set and not the child's — a $PreviousUser grant
        // missing from the hint can never match.
        var evaluator = await CreateEvaluatorAsync(
            instance,
            workflow,
            requestContext,
            candidates.SelectMany(c =>
                EffectiveTransitionGrants(instance, c.Transition).Concat(c.StateEntry?.Roles ?? [])),
            cancellationToken);

        var result = new List<string>(candidates.Count);
        foreach (var (key, transition, stateEntry) in candidates)
        {
            var stamped = instance is null
                ? null
                : SubFlowTransitionOverrideReader.TryReadRoles(instance, transition.Key);

            var allowed = stamped is { Count: > 0 }
                ? evaluator.IsAnyRoleAllowed(callerRoles, stamped, transition)
                : IsAllowedWithStateNarrowing(evaluator, callerRoles, transition, stateEntry);

            if (allowed)
                result.Add(key);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> IsRoleAllowedForGrantsAsync(
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

    /// <summary>
    /// True when the key is declared on at least one state, i.e. it is a state transition rather than
    /// a shared or well-known one. Those two families are the only ones `availableIn` describes.
    /// </summary>
    private static bool IsStateScoped(WorkflowDefinition workflow, string transitionKey) =>
        workflow.States.Any(s => s.Transitions
            .Any(t => string.Equals(t.Key, transitionKey, StringComparison.Ordinal)));

    /// <inheritdoc />
    public async Task<bool> IsQueryAllowedAsync(
        WorkflowDefinition workflow,
        Instance instance,
        IReadOnlyCollection<string>? callerRoles,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        // The instance's OWN state, never its EffectiveState.
        //
        // EffectiveState is the DEEPEST active subflow's state key, which the parent's workflow does
        // not contain — so `FindState` returned null and the stamped override (keyed by the state the
        // parent declared for its child) could not match either, and the gate silently fell through to
        // the workflow root's grants. Measured on the bench: CurrentState=mid-waiting /
        // EffectiveState=leaf-waiting on a mid, and a caller the parent had narrowed away still read
        // it 200. That made BOTH a SubFlow state's own queryRoles and a parent's overrides.states
        // narrowing inert for exactly as long as the child had a subflow of its own — a written
        // restriction that stopped applying with no error and no log.
        //
        // The human-task resolver already resolved its grants from CurrentState
        // (`HumanTaskLeafResolver.ResolveLeafAsync`), so the two paths disagreed about the same
        // instance; this is the side that was wrong.
        var currentStateKey = instance.GetCurrentState;
        var state = string.IsNullOrWhiteSpace(currentStateKey) ? null : workflow.FindState(currentStateKey);

        // Precedence: the parent's stamped state override, then the state's own queryRoles, then the
        // workflow root's.
        //
        // The override goes first and it REPLACES rather than merges — a parent that narrowed a
        // child's visibility meant to narrow it. Reading the stamp here rather than in each surface
        // is the point: this method is the single queryRoles answer behind `authorize`'s query branch
        // (which the gateway consults for the state, data, view, schema and incident reads) and
        // behind the human-task list, so the narrowing applies wherever the child is reached from. The parent-side
        // reader (`AuthorizeAppService`, `subFlowConfig.Overrides.States`) cannot serve that: it
        // needs an active SubFlow correlation, which the child being asked about does not have.
        var overridden = string.IsNullOrWhiteSpace(currentStateKey)
            ? null
            : SubFlowStateOverrideReader.TryReadQueryRoles(instance, currentStateKey);

        var queryRoles = overridden is { Count: > 0 }
            ? overridden
            : state is { QueryRoles.Count: > 0 } ? state.QueryRoles : workflow.QueryRoles;

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
        => EvaluateRolesStatic(
            role is null ? [] : [role], roleGrants, defaultAllowWhenNoAllowGrant);

    /// <summary>
    /// The multi-role form, and the one <see cref="IRoleGrantEvaluator"/> degrades to when it holds
    /// no instance. Same two phases as the instance-bound path: the DENY group is an AND evaluated
    /// first, the ALLOW group an OR evaluated only if nothing denied.
    /// </summary>
    /// <remarks>
    /// This twin must move whenever the instance-bound evaluator moves. They are two spellings of
    /// one rule, and the equivalence is asserted by <c>RoleGrantEvaluatorTests</c> — letting them
    /// drift would mean an authorization answer that depends on whether an instance happened to be
    /// loaded, which is not a distinction any caller can see or reason about.
    /// <para>
    /// Static comparison only: with no instance there is nothing for a predefined or dynamic grant
    /// to resolve against, so those grants simply never match here.
    /// </para>
    /// </remarks>
    public static bool EvaluateRolesStatic(
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<RoleGrant> roleGrants,
        bool defaultAllowWhenNoAllowGrant = true)
    {
        if (roleGrants.Count == 0)
            return true; // No roles defined → allow

        var normalized = new List<string>(roles.Count);
        foreach (var role in roles)
        {
            if (!string.IsNullOrWhiteSpace(role))
                normalized.Add(role.Trim());
        }

        // Phase 1 - DENY group, AND. One matching deny refuses, whatever else the caller carries —
        // and a role-bound deny refuses a caller with no roles at all (see IsUnprovableRoleBoundDeny).
        foreach (var grant in roleGrants)
        {
            if (grant.IsDeny &&
                (IsUnprovableRoleBoundDeny(grant, normalized.Count) || MatchesAnyStatic(grant, normalized)))
                return false;
        }

        // Phase 2 - ALLOW group, OR.
        var hasAllowGrant = false;
        foreach (var grant in roleGrants)
        {
            if (!grant.IsAllow)
                continue;

            hasAllowGrant = true;
            if (MatchesAnyStatic(grant, normalized))
                return true;
        }

        // Blacklist (deny-only) set: no ALLOW grant defined → allow when not explicitly denied.
        return defaultAllowWhenNoAllowGrant && !hasAllowGrant;
    }

    /// <summary>
    /// Whether a DENY grant must refuse because the caller carries no roles to check it against.
    /// </summary>
    /// <remarks>
    /// <para><b>A role-bound deny cannot be cleared by an empty role set.</b> A static role or a
    /// <c>$role.</c> reference is a statement about the caller's ROLES; with none to compare,
    /// "nothing matched" is not evidence that the caller is not the denied one. Read as a pass it
    /// made every blacklist a blanket allow for a token minted by a role-less process, and — once
    /// an external provider's failure resolves to an empty set — for any caller whose roles could
    /// not be fetched. The grant author wrote a refusal; the runtime must not be the one to waive it.</para>
    /// <para><b>Identity-bound denies keep their normal evaluation.</b> Predefined roles
    /// (<c>$InstanceStarter</c>, …) and <c>$user.</c> / <c>$userBehalfOf.</c> references match on the
    /// caller's identity, not on its roles, so the absence of roles hides nothing from them.</para>
    /// <para>The classification mirrors <see cref="RoleGrantEvaluator"/>'s match order (predefined,
    /// then dynamic, then static) so a grant is role-bound here exactly when it would be compared
    /// against a role there — including a malformed dynamic grant, which falls through to the static
    /// comparison and is therefore role-bound.</para>
    /// </remarks>
    /// <param name="grant">The grant under evaluation; only DENY grants are meaningful here.</param>
    /// <param name="normalizedRoleCount">The caller's role count after blank roles are dropped.</param>
    internal static bool IsUnprovableRoleBoundDeny(RoleGrant grant, int normalizedRoleCount)
        => normalizedRoleCount == 0 && grant.IsDeny && IsRoleBound(grant.Role);

    private static bool IsRoleBound(string? grantRole)
    {
        if (string.IsNullOrWhiteSpace(grantRole))
            return true;

        if (grantRole is PredefinedInstanceRoles.InstanceStarter
            or PredefinedInstanceRoles.PreviousUser
            or PredefinedInstanceRoles.InstanceBehalfOfStarter
            or PredefinedInstanceRoles.PreviousBehalfOfUser)
            return false;

        var dynamicGrant = DynamicRoleGrant.TryParse(grantRole);
        return dynamicGrant is null || dynamicGrant.Qualifier == DynamicRoleQualifier.Role;
    }

    private static bool MatchesAnyStatic(RoleGrant grant, List<string> roles)
    {
        foreach (var role in roles)
        {
            if (string.Equals(grant.Role, role, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
