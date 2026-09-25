using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Application service for authorize and authorization matrix system functions.
/// Evaluates role grants: DENY always wins; if no DENY match, any ALLOW match yields allowed.
/// The caller's role set comes from the configured provider via <see cref="ICallerRoleResolver"/>
/// (multiple roles); if any caller role is allowed, result is allowed.
/// When instance has active subflow, forwards authorize request to subflow via IAuthorizeGateway.
/// For predefined roles ($InstanceStarter, $PreviousUser), matching is done against ICurrentUser.ActorUserName.
/// </summary>
public sealed class AuthorizeAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    ITransitionAuthorizationManager transitionAuthorizationManager,
    IAuthorizeGateway authorizeGateway,
    ICallerRoleResolver callerRoleResolver,
    Execution.LongPoll.ILongPollInteractionGate longPollInteractionGate,
    ILogger<AuthorizeAppService> logger) : ApplicationService(serviceProvider), IAuthorizeAppService
{
    /// <inheritdoc />
    public async Task<Result<AuthorizeOutput>> GetAuthorizeResultForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string role,
        string? transitionKey,
        string? functionKey,
        string? version = null,
        bool checkQueryRoles = false,
        bool checkAck = false,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateAuthorizeTargetInstance(transitionKey, functionKey, checkQueryRoles, checkAck);
        if (validation.HasValue)
            return validation.Value;

        runtimeInfoProvider.Check(domain);
        var workflowVersion = NormalizeVersion(version);
        var workflowResult = await componentCacheStore.GetFlowAsync(domain, workflow, workflowVersion, cancellationToken);
        if (!workflowResult.IsSuccess)
            return Result<AuthorizeOutput>.Fail(workflowResult.Error);

        Instance? instance = null;
        var instanceResult = await instanceRepository.FindByIdentifierAsync(instanceId, cancellationToken);
        if (instanceResult is not null)
            instance = instanceResult;

        var wf = workflowResult.Value!;

        // An instance with an active SubFlow answers in one of two ways: a transition the parent
        // RETAINS is answered here, everything else descends the active-correlation chain.
        if (instance?.Subflow != null)
        {
            var subflow = instance.Subflow;

            // Parent-retained transitions execute on the parent, so authorize must answer against the
            // parent's definition for exactly those and must not descend. The set matches execution:
            // HandleCancelPreflightStep (order 5) skips to CreateTransition (20), over the forward at
            // order 10, for cancel and exit; ForwardToActiveSubflowStep excludes updateData and a
            // shared transition available in the current state. A shared transition NOT available here
            // is not forwarded either — it is rejected — and EvaluateAuthorizeAsync denies it on the
            // same availableIn check, so the two surfaces agree without sharing code.
            if (!string.IsNullOrWhiteSpace(transitionKey) && IsParentOwnedTransition(wf, transitionKey))
            {
                var parentCallerRoles = await GetCallerRolesAsync(role, requestContext, cancellationToken);
                if (!parentCallerRoles.IsSuccess)
                    return Result<AuthorizeOutput>.Fail(parentCallerRoles.Error);
                var parentAllowed = await EvaluateAuthorizeAsync(wf, parentCallerRoles.Value, transitionKey, null, instance, false, false, domain, workflowVersion, requestContext, cancellationToken);
                logger.AuthorizeRequest(domain, workflow, instanceId, DescribeTarget(transitionKey, null, false, false), Describe(parentCallerRoles.Value), parentAllowed);
                return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = parentAllowed });
            }

            // Forward to SubFlow (functionKey, subflow-owned transition, queryRoles)
            var resolvedForForward = await GetCallerRolesAsync(role, requestContext, cancellationToken);
            if (!resolvedForForward.IsSuccess)
                return Result<AuthorizeOutput>.Fail(resolvedForForward.Error);

            // ack mirrors the acknowledge endpoint's OWN descent rule, which is not "has a subflow"
            // but "is this the instance that paused": InstanceCommandAppService descends only while
            // !IsAwaitingLongPollAck. When this instance is the awaiting one, the gate is evaluated
            // here and the chain below it is irrelevant — a deeper child is not what the ack resumes.
            if (checkAck && instance.IsAwaitingLongPollAck)
            {
                var ackCallerRoles = await GetAckCallerRolesAsync(role, requestContext, cancellationToken);
                if (!ackCallerRoles.IsSuccess)
                    return Result<AuthorizeOutput>.Fail(ackCallerRoles.Error);
                var ackAllowed = await EvaluateAckAsync(wf, ackCallerRoles.Value, instance, requestContext, cancellationToken);
                logger.AuthorizeRequest(domain, workflow, instanceId, DescribeTarget(null, null, false, true), Describe(ackCallerRoles.Value), ackAllowed);
                return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = ackAllowed });
            }

            // queryRoles is a CONJUNCTION down the chain, because that is what the read surfaces
            // enforce: the state function gates the polled instance (InstanceQueryAppService) and THEN
            // descends, where the leaf gates again — two conjunctive gates. Answering from the leaf
            // alone made `authorize` strictly weaker than the gate it exists to describe, so a gateway
            // trusting it would admit callers the runtime itself refuses. The root's own verdict is
            // taken here; the forward below re-enters this method at the child, which repeats it for
            // its own level, so the whole chain down to the deepest active leaf is ANDed.
            if (checkQueryRoles)
            {
                var rootAllowed = await transitionAuthorizationManager.IsQueryAllowedAsync(
                    wf, instance, resolvedForForward.Value, requestContext, cancellationToken);
                if (!rootAllowed)
                {
                    logger.AuthorizeRequest(domain, workflow, instanceId, DescribeTarget(null, null, true, false), Describe(resolvedForForward.Value), false);
                    return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = false });
                }
            }

            // Parent-declared overrides are deliberately NOT read here. They are stamped onto the child
            // when it starts (SubflowStarter) and resolved at the level they govern by the single
            // resolver (TransitionAuthorizationManager, via SubFlowStateOverrideReader /
            // SubFlowTransitionOverrideReader). Reading them from the parent's definition here did two
            // things wrong: it RETURNED at depth 1, so a grandchild's own gate never ran; and it could
            // not work at all for a directly addressed leaf, where no parent is in scope — which is how
            // `authorize` and the state function came to give opposite verdicts for the same leaf.
            // Per-hop stamping also gives the authored semantics for free: a parent's override of its
            // child never reaches the grandchild, because only the direct parent's map is stamped.
            var roleForForward = resolvedForForward.Value is { Count: > 0 } forwardRoles
                ? string.Join(",", forwardRoles)
                : role;

            // Only the forward is spanned, not the whole method. Everything above answered locally —
            // parent-owned transitions, transition and queryRole overrides — and never touched the
            // subflow. Spanning the method would report a descent that did not happen, and
            // "this trace has no Subflow.Descend" would stop meaning "nothing descended".
            using var descent = InstanceReadActivityHelper.StartDescendScope(
                runtimeInfoProvider,
                subflow.SubFlowDomain,
                subflow.SubFlowName,
                subflow.SubFlowInstanceId.ToString(),
                instance!.Id.ToString(),
                TelemetryConstants.DescentFunctions.Authorize);

            return await authorizeGateway.GetAuthorizeResultForInstanceAsync(
                subflow.SubFlowDomain,
                subflow.SubFlowName,
                subflow.SubFlowInstanceId.ToString(),
                roleForForward,
                transitionKey,
                functionKey,
                version,
                checkQueryRoles,
                checkAck,
                requestContext,
                cancellationToken);
        }

        // `ack` composes the role parameter ADDITIVELY on every path, like the awaiting-parent branch
        // above; every other target keeps it as a fallback. Resolving ack through the fallback here
        // gave the same caller a different role set depending on whether the instance had a SubFlow.
        var callerRolesResult = checkAck
            ? await GetAckCallerRolesAsync(role, requestContext, cancellationToken)
            : await GetCallerRolesAsync(role, requestContext, cancellationToken);
        if (!callerRolesResult.IsSuccess)
            return Result<AuthorizeOutput>.Fail(callerRolesResult.Error);
        // The decision itself. Everything around it was already traceable — role resolution, the
        // subflow forward, the instance load — while the answer they exist to produce was not, so a
        // denial could be seen arriving and never explained.
        using var decision = AuthorizationActivityHelper.StartDecide();
        var allowed = await EvaluateAuthorizeAsync(wf, callerRolesResult.Value, transitionKey, functionKey, instance, checkQueryRoles, checkAck, domain, workflowVersion, requestContext, cancellationToken);
        AuthorizationActivityHelper.SetDecision(decision, allowed, callerRolesResult.Value?.Count ?? 0);

        logger.AuthorizeRequest(domain, workflow, instanceId, DescribeTarget(transitionKey, functionKey, checkQueryRoles, checkAck), Describe(callerRolesResult.Value), allowed);
        return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = allowed });
    }

    /// <summary>
    /// Resolves the caller's role set through the configured provider and composes the explicit
    /// <c>role</c> request parameter the way the provider states
    /// (<see cref="ICallerRoleResolver.RoleParameterMode"/>): as a fallback when the provider reports no
    /// roles, or as the request's <c>role</c> header when the request carries none.
    /// <para>
    /// The provider is asked first so that this surface and the discovery surfaces agree: an
    /// <c>authorize</c> answer that contradicts <c>availableTransitions</c> is worse than no answer.
    /// The <c>role</c> parameter remains a convenience for callers probing a specific role, and under
    /// the default provider it behaves exactly as before — that provider returns the current user's
    /// roles, then the <c>role</c> header, and only an empty result lets the parameter through.
    /// </para>
    /// </summary>
    /// <summary>
    /// Renders the role set for the audit log. The resolved set is logged rather than the <c>role</c>
    /// request parameter — under an external provider that parameter is usually absent, and logging it
    /// would record an empty role for every decision.
    /// </summary>
    private static string Describe(IReadOnlyList<string>? roles) =>
        roles is { Count: > 0 } ? string.Join(",", roles) : string.Empty;

    /// <summary>
    /// Names which of the four questions was asked, for the audit line. Without it the log records the
    /// same domain/workflow/roles tuple for every authorize call a caller makes against a flow, so a
    /// refusal cannot be told from any other refusal.
    /// </summary>
    private static string DescribeTarget(string? transitionKey, string? functionKey, bool checkQueryRoles, bool checkAck)
    {
        if (!string.IsNullOrWhiteSpace(transitionKey)) return $"transition:{transitionKey}";
        if (!string.IsNullOrWhiteSpace(functionKey)) return $"function:{functionKey}";
        if (checkAck) return "ack";
        if (checkQueryRoles) return "queryRoles";
        return "none";
    }

    private async Task<Result<IReadOnlyList<string>?>> GetCallerRolesAsync(
        string? roleParameter,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        // AsRoleHeader: the parameter is the request's role header when there is none, and the
        // resolver's own header precedence decides — no fallback on top.
        if (callerRoleResolver.RoleParameterMode == RoleParameterMode.AsRoleHeader)
        {
            var asHeader = await callerRoleResolver.ResolveRolesAsync(
                WithRoleParameterAsHeader(requestContext?.Headers, roleParameter), cancellationToken);
            return asHeader.IsSuccess
                ? Result<IReadOnlyList<string>?>.Ok(asHeader.Value is { Length: > 0 } r ? r : null)
                : Result<IReadOnlyList<string>?>.Fail(asHeader.Error);
        }

        var resolved = await callerRoleResolver.ResolveRolesAsync(requestContext?.Headers, cancellationToken);
        if (!resolved.IsSuccess)
            return Result<IReadOnlyList<string>?>.Fail(resolved.Error);

        if (resolved.Value is { Length: > 0 } roles)
            return Result<IReadOnlyList<string>?>.Ok(roles);

        // Fallback: the provider answered "none", so the parameter stands in.
        return Result<IReadOnlyList<string>?>.Ok(
            string.IsNullOrWhiteSpace(roleParameter) ? null : [roleParameter.Trim()]);
    }

    /// <summary>
    /// The request headers with the <c>role</c> parameter in the <c>role</c> header's place — only when
    /// the request carries no non-blank <c>role</c> header of its own, which then wins. Returns the
    /// original dictionary when there is nothing to add.
    /// </summary>
    private static IReadOnlyDictionary<string, string?>? WithRoleParameterAsHeader(
        IReadOnlyDictionary<string, string?>? headers,
        string? roleParameter)
    {
        if (string.IsNullOrWhiteSpace(roleParameter))
            return headers;
        if (headers is not null
            && headers.TryGetValue(AetherClaimTypes.Role, out var existing)
            && !string.IsNullOrWhiteSpace(existing))
            return headers;

        var merged = headers is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(headers, StringComparer.OrdinalIgnoreCase);
        merged[AetherClaimTypes.Role] = roleParameter.Trim();
        return merged;
    }

    private async Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixAsync(
        string domain,
        string workflow,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);
        var workflowVersion = NormalizeVersion(version);
        var workflowResult = await componentCacheStore.GetFlowAsync(domain, workflow, workflowVersion, cancellationToken);
        if (!workflowResult.IsSuccess)
            return Result<AuthorizationMatrixOutput>.Fail(workflowResult.Error);

        var wf = workflowResult.Value!;
        var output = new AuthorizationMatrixOutput
        {
            Workflow = wf.Key,
            QueryRoles = ToRoleGrantDtos(wf.QueryRoles),
            States = wf.States.Select(s => new AuthorizationMatrixStateDto
            {
                Key = s.Key,
                QueryRoles = ToRoleGrantDtos(s.QueryRoles)
            }).ToList(),
            Transitions = [],
            Functions = []
        };

        // Collect transitions: shared, start, cancel, updateData, exit, and from each state
        var transitionKeys = new HashSet<string>();
        AddTransition(output.Transitions, transitionKeys, wf.StartTransition);
        if (wf.Cancel != null)
            AddTransition(output.Transitions, transitionKeys, wf.Cancel);
        if (wf.UpdateData != null)
            AddTransition(output.Transitions, transitionKeys, wf.UpdateData);
        if (wf.Exit != null)
            AddTransition(output.Transitions, transitionKeys, wf.Exit);
        foreach (var t in wf.SharedTransitions)
            AddTransition(output.Transitions, transitionKeys, t);
        foreach (var state in wf.States)
            foreach (var t in state.Transitions)
                AddTransition(output.Transitions, transitionKeys, t);

        // Functions: workflow-referenced functions with their roles
        foreach (var fnRef in wf.Functions)
        {
            var fnResult = await componentCacheStore.GetFunctionAsync(domain, fnRef.Key, fnRef.Version, cancellationToken);
            if (!fnResult.IsSuccess)
                continue;
            var fn = fnResult.Value!;
            output.Functions.Add(new AuthorizationMatrixFunctionDto
            {
                Key = fn.Key,
                Roles = ToRoleGrantDtos(fn.Roles)
            });
        }

        logger.AuthorizationMatrixRequest(domain, workflow);
        return Result<AuthorizationMatrixOutput>.Ok(output);
    }

    /// <inheritdoc />
    public async Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);

        var workflowVersion = NormalizeVersion(version);
        var workflowResult = await componentCacheStore.GetFlowAsync(domain, workflow, workflowVersion, cancellationToken);
        if (!workflowResult.IsSuccess)
            return Result<AuthorizationMatrixOutput>.Fail(workflowResult.Error);
        var wf = workflowResult.Value!;

        Instance? instance = null;
        var instanceResult = await instanceRepository.FindByIdentifierAsync(instanceId, cancellationToken);
        if (instanceResult is not null)
            instance = instanceResult;

        if (instance?.Subflow != null)
        {
            var subflow = instance.Subflow;

            // The sibling of the authorize forward above, which has had this span since the
            // descent ladder was introduced. This one never did, so the matrix was the single
            // subflow forward in this service a trace could not show — and it is the more
            // expensive of the two, since it resolves every function the workflow declares.
            using var descent = InstanceReadActivityHelper.StartDescendScope(
                runtimeInfoProvider,
                subflow.SubFlowDomain,
                subflow.SubFlowName,
                subflow.SubFlowInstanceId.ToString(),
                instance.Id.ToString(),
                TelemetryConstants.DescentFunctions.Matrix);

            var subFlowMatrixResult = await authorizeGateway.GetAuthorizationMatrixForInstanceAsync(
                subflow.SubFlowDomain,
                subflow.SubFlowName,
                subflow.SubFlowInstanceId.ToString(),
                version,
                cancellationToken);

            if (!subFlowMatrixResult.IsSuccess)
                return subFlowMatrixResult;

            var matrix = subFlowMatrixResult.Value!;
            var parentState = wf.FindState(instance.CurrentState!);
            var subFlowConfig = parentState?.SubFlow;

            // Apply transition overrides from parent config (replace mode)
            if (subFlowConfig?.HasTransitionRoleOverrides == true)
            {
                foreach (var t in matrix.Transitions)
                {
                    if (subFlowConfig.Overrides!.Transitions!.TryGetValue(t.Key, out var tOverride) &&
                        tOverride.Roles is { Count: > 0 })
                        t.Roles = ToRoleGrantDtos(tOverride.Roles!);
                }
            }

            // Apply state queryRole overrides from parent config (replace mode)
            if (subFlowConfig?.HasQueryRoleOverrides == true)
            {
                foreach (var s in matrix.States)
                {
                    if (subFlowConfig.Overrides!.States!.TryGetValue(s.Key, out var sOverride) &&
                        sOverride.QueryRoles is { Count: > 0 })
                        s.QueryRoles = ToRoleGrantDtos(sOverride.QueryRoles!);
                }
            }

            // Merge parent-owned transitions (shared, cancel, updateData, exit) not already in SubFlow matrix
            var existingTransitionKeys = new HashSet<string>(
                matrix.Transitions.Select(t => t.Key), StringComparer.Ordinal);
            foreach (var t in wf.SharedTransitions)
                AddTransition(matrix.Transitions, existingTransitionKeys, t);
            if (wf.Cancel != null) AddTransition(matrix.Transitions, existingTransitionKeys, wf.Cancel);
            if (wf.UpdateData != null) AddTransition(matrix.Transitions, existingTransitionKeys, wf.UpdateData);
            if (wf.Exit != null) AddTransition(matrix.Transitions, existingTransitionKeys, wf.Exit);

            logger.AuthorizationMatrixRequest(domain, workflow);
            return Result<AuthorizationMatrixOutput>.Ok(matrix);
        }

        return await GetAuthorizationMatrixAsync(domain, workflow, version, cancellationToken);
    }

    private static void AddTransition(
        List<AuthorizationMatrixTransitionDto> list,
        HashSet<string> seenKeys,
        Transition t)
    {
        if (seenKeys.Add(t.Key))
            list.Add(new AuthorizationMatrixTransitionDto
            {
                Key = t.Key,
                From = t.From,
                Target = t.Target,
                Roles = ToRoleGrantDtos(t.Roles),
                AvailableIn = ToAvailableInDtos(t.AvailableIn)
            });
    }

    /// <summary>Maps availableIn entries to DTOs; returns empty list when none (schema consistency).</summary>
    private static List<AuthorizationMatrixAvailableInDto> ToAvailableInDtos(IReadOnlyCollection<AvailableInEntry> entries)
    {
        if (entries.Count == 0)
            return [];
        return entries
            .Select(e => new AuthorizationMatrixAvailableInDto
            {
                State = e.State,
                Roles = ToRoleGrantDtos(e.Roles)
            })
            .ToList();
    }

    /// <summary>
    /// Returns true if the transition key belongs to the parent workflow's own transitions
    /// (shared, cancel, updateData, exit) that should be evaluated locally regardless of active SubFlow.
    /// </summary>
    private static bool IsParentOwnedTransition(Definitions.Workflow wf, string transitionKey) =>
        wf.SharedTransitions.Any(t => t.Key == transitionKey) ||
        wf.Cancel?.Key == transitionKey ||
        wf.UpdateData?.Key == transitionKey ||
        wf.Exit?.Key == transitionKey;

    /// <summary>Maps role grants to DTOs; returns empty list when none (schema consistency).</summary>
    private static List<RoleGrantDto> ToRoleGrantDtos(IReadOnlyCollection<RoleGrant> roles)
    {
        if (roles.Count == 0)
            return [];
        return roles.Select(r => new RoleGrantDto { Role = r.Role, Grant = r.Grant }).ToList();
    }

    /// <summary>
    /// Normalizes version query param: null or whitespace → null (latest); otherwise trimmed.
    /// </summary>
    private static string? NormalizeVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? null : version.Trim();

    /// <summary>
    /// Validates instance-level authorize: exactly one of transitionKey, functionKey, checkQueryRoles or checkAck.
    /// </summary>
    private static Result<AuthorizeOutput>? ValidateAuthorizeTargetInstance(string? transitionKey, string? functionKey, bool checkQueryRoles, bool checkAck)
    {
        var hasTransition = !string.IsNullOrWhiteSpace(transitionKey);
        var hasFunction = !string.IsNullOrWhiteSpace(functionKey);
        var count = (hasTransition ? 1 : 0) + (hasFunction ? 1 : 0) + (checkQueryRoles ? 1 : 0) + (checkAck ? 1 : 0);
        if (count != 1)
            return Result<AuthorizeOutput>.Fail(WorkflowErrors.AuthorizeRequiresExactlyOneTarget());
        return null;
    }

    /// <summary>
    /// Evaluates authorize for a single target: transition, function, or state-based query roles.
    /// If any caller role is allowed, returns true. Caller validates exactly one target is specified.
    /// Resolves predefined instance roles ($InstanceStarter, $PreviousUser) when instance is present.
    /// </summary>
    private Task<bool> EvaluateAuthorizeAsync(
        Definitions.Workflow workflow,
        IReadOnlyList<string>? callerRoles,
        string? transitionKey,
        string? functionKey,
        Instance? instance,
        bool checkQueryRoles,
        bool checkAck,
        string domain,
        string? workflowVersion,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
        // ONE evaluation with the whole role set. This used to loop the caller's roles and return
        // on the first one that was allowed — the canonical rule's composition rebuilt a layer up,
        // and the wrong one: the deny group is an AND across every role the caller carries, so
        // asking role by role lets an allowed role answer before a denied one is ever considered.
        => EvaluateAuthorizeCoreAsync(
            workflow, callerRoles, transitionKey, functionKey, instance,
            checkQueryRoles, checkAck, domain, workflowVersion, requestContext, cancellationToken);

    private async Task<bool> EvaluateAuthorizeCoreAsync(
        Definitions.Workflow workflow,
        IReadOnlyCollection<string>? callerRoles,
        string? transitionKey,
        string? functionKey,
        Instance? instance,
        bool checkQueryRoles,
        bool checkAck,
        string domain,
        string? workflowVersion,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        if (checkQueryRoles)
            return await EvaluateQueryRolesAsync(workflow, callerRoles, instance, requestContext, cancellationToken);

        if (checkAck)
            return await EvaluateAckAsync(workflow, callerRoles, instance, requestContext, cancellationToken);

        if (!string.IsNullOrWhiteSpace(transitionKey))
        {
            var transition = workflow.FindTransitionInContext(transitionKey);
            if (transition == null)
                return false;

            // State-aware: the transition's availableIn must offer the instance's current state, and any
            // grants that entry adds for the state narrow the transition's own grants (AND). Previously
            // this ignored the state entirely, so authorize answered "allowed" for a transition the
            // execution policy then rejected with Transition:100021 — the two surfaces disagreed.
            // A null instance (workflow-scoped authorize) has no state in scope and is unaffected.
            // CurrentState, not GetEffectiveState: availableIn lists states of THIS workflow, while
            // EffectiveState can carry an active subflow's state key, which the parent definition does
            // not contain — that would deny every parent transition. The discovery surface keys off
            // CurrentState too (GetMainFlowTransitions / MergeWithParentAvailableTransitions).
            return await transitionAuthorizationManager.IsTransitionAllowedInStateAsync(
                workflow,
                transition,
                instance?.CurrentState,
                instance,
                callerRoles,
                requestContext,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(functionKey))
        {
            var fnResult = await componentCacheStore.GetFunctionAsync(domain, functionKey, workflowVersion, cancellationToken);
            if (!fnResult.IsSuccess)
                return false;
            if (fnResult.Value!.Roles.Count == 0)
                return true; // No roles defined on function → allow
            return await transitionAuthorizationManager.IsRoleAllowedForGrantsAsync(callerRoles, fnResult.Value!.Roles, instance, requestContext, cancellationToken);
        }

        return false;
    }

    // EvaluateWithGrantsAsync was removed with the parent-side override branches it served. Applying
    // "SubFlow override grants locally without forwarding to the SubFlow" is precisely what could not
    // work: it returned at depth 1 and had nothing to read at a directly addressed leaf. The overrides
    // are now resolved at the level they govern, from the child's stamp, by TransitionAuthorizationManager.

    /// <summary>
    /// Evaluates state-based query roles: instance effective state → state queryRoles or workflow root queryRoles.
    /// Resolves predefined instance roles ($InstanceStarter, $PreviousUser) when instance is present. DENY wins, else ALLOW.
    /// </summary>
    private async Task<bool> EvaluateQueryRolesAsync(
        Definitions.Workflow workflow,
        IReadOnlyCollection<string>? callerRoles,
        Instance? instance,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        if (instance == null)
            return false;
        return await transitionAuthorizationManager.IsQueryAllowedAsync(
            workflow, instance, callerRoles, requestContext, cancellationToken);
    }

    /// <summary>
    /// Evaluates the long-poll acknowledge gate — the pre-flight for
    /// <c>POST .../instances/{instance}/longpoll/ack</c>.
    /// <para>
    /// Answered through <see cref="Execution.LongPoll.ILongPollInteractionGate"/>, the same object the
    /// acknowledge endpoint and the state function's signal emit admit through, so the arm selection
    /// (rule, else roles, else allow) cannot diverge between the pre-flight and the call it describes.
    /// Re-implementing the roles arm here would silently ignore the <c>rule</c> arm, which is a C#
    /// script no middle tier can evaluate — the one thing that must not be lost in a pre-flight.
    /// </para>
    /// <para>
    /// A caller asking about an instance that is <b>not</b> awaiting an acknowledgement is answered
    /// <c>true</c>, because that is what the endpoint does: it returns <c>Ok()</c> idempotently when
    /// nothing in the chain is awaiting. Answering <c>false</c> would have the middle tier refuse a
    /// call the runtime accepts.
    /// </para>
    /// </summary>
    private async Task<bool> EvaluateAckAsync(
        Definitions.Workflow workflow,
        IReadOnlyCollection<string>? callerRoles,
        Instance? instance,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        if (instance is null)
            return false;

        if (!instance.IsAwaitingLongPollAck)
            return true;

        var state = workflow.FindState(instance.GetCurrentState);

        var admitted = await longPollInteractionGate.IsAdmittedAsync(
            instance,
            workflow,
            state,
            requestContext?.Headers is null ? null : new Dictionary<string, string?>(requestContext.Headers),
            requestContext?.QueryParameters is null ? null : new Dictionary<string, string?>(requestContext.QueryParameters),
            _ => Task.FromResult(Result<IReadOnlyCollection<string>>.Ok(callerRoles ?? [])),
            surface: "authorize",
            cancellationToken);

        // A gate failure is a resolution failure, not a denial; it cannot reach here because the roles
        // are already resolved and handed in, but the contract is honoured rather than assumed.
        return admitted.IsSuccess && admitted.Value;
    }

    /// <summary>
    /// The caller role set for the <c>ack</c> target. Under a <see cref="RoleParameterMode.Fallback"/>
    /// provider the explicit <c>role</c> parameter is <b>additive</b> to the provider's roles rather than
    /// a fallback; under <see cref="RoleParameterMode.AsRoleHeader"/> it is the role header, like every
    /// other target (<see cref="ICallerRoleResolver.RoleParameterMode"/>).
    /// <para>
    /// <b>Every <c>ack</c> path uses it</b> — the awaiting instance with an active SubFlow and the one
    /// without (the common path). Until 2026-09-25 the second went through
    /// <see cref="GetCallerRolesAsync"/>, where a provider that answered with roles discarded the
    /// parameter, so the same caller got a different role set depending on whether the instance had a
    /// SubFlow. Pinned by <c>AuthorizeRoleParameterFallbackTests</c>.
    /// </para>
    /// <para>
    /// <b>Every <c>ack</c> path uses it</b> — the awaiting instance with an active SubFlow and the one
    /// without (the common path). Until 2026-09-25 the second went through
    /// <see cref="GetCallerRolesAsync"/>, where a provider that answered with roles discarded the
    /// parameter, so the same caller got a different role set depending on whether the instance had a
    /// SubFlow. Pinned by <c>AuthorizeRoleParameterFallbackTests</c>.
    /// </para>
    /// <para>
    /// This deliberately differs from <see cref="GetCallerRolesAsync"/>, which the other targets use.
    /// It preserves how the acknowledge endpoint itself used to build the set, back when that endpoint
    /// still evaluated the gate: the explicit parameter is how a client names WHICH of its roles is
    /// acknowledging, so dropping it whenever the provider answers anything would silently change the
    /// question. The endpoint no longer authorizes — this pre-flight is the only evaluation left — so
    /// there is no second implementation to agree with, and this one is now the definition.
    /// </para>
    /// </summary>
    private async Task<Result<IReadOnlyList<string>?>> GetAckCallerRolesAsync(
        string? roleParameter,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        // Under AsRoleHeader ack follows the same header rule as every other target.
        if (callerRoleResolver.RoleParameterMode == RoleParameterMode.AsRoleHeader)
            return await GetCallerRolesAsync(roleParameter, requestContext, cancellationToken);

        var resolved = await callerRoleResolver.ResolveRolesAsync(requestContext?.Headers, cancellationToken);
        if (!resolved.IsSuccess)
            return Result<IReadOnlyList<string>?>.Fail(resolved.Error);

        var roles = new List<string>();

        // Fallback provider: additive on every ack path.
        if (!string.IsNullOrWhiteSpace(roleParameter))
            roles.Add(roleParameter.Trim());

        if (resolved.Value is { Length: > 0 } providerRoles)
            roles.AddRange(providerRoles.Where(r => !roles.Contains(r, StringComparer.OrdinalIgnoreCase)));

        return Result<IReadOnlyList<string>?>.Ok(roles.Count == 0 ? null : roles);
    }
}
