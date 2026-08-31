using BBT.Aether.Application.Services;
using BBT.Aether.Results;
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
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateAuthorizeTargetInstance(transitionKey, functionKey, checkQueryRoles);
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

        // When instance has active subflow, distinguish parent-owned vs SubFlow-owned requests
        if (instance?.Subflow != null)
        {
            var subflow = instance.Subflow;
            var parentState = wf.FindState(instance.CurrentState!);
            var subFlowConfig = parentState?.SubFlow;

            // transitionKey: parent-owned (shared/cancel/updateData/exit) → evaluate locally against parent workflow
            if (!string.IsNullOrWhiteSpace(transitionKey))
            {
                if (IsParentOwnedTransition(wf, transitionKey))
                {
                    var parentCallerRoles = await GetCallerRolesAsync(role, requestContext, cancellationToken);
                    if (!parentCallerRoles.IsSuccess)
                        return Result<AuthorizeOutput>.Fail(parentCallerRoles.Error);
                    var parentAllowed = await EvaluateAuthorizeAsync(wf, parentCallerRoles.Value, transitionKey, null, instance, false, domain, workflowVersion, requestContext, cancellationToken);
                    logger.AuthorizeRequest(domain, workflow, Describe(parentCallerRoles.Value), parentAllowed);
                    return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = parentAllowed });
                }

                // SubFlow-owned transition: check override first, then forward
                if (subFlowConfig?.HasTransitionRoleOverrides == true &&
                    subFlowConfig.Overrides!.Transitions!.TryGetValue(transitionKey, out var transitionOverride) &&
                    transitionOverride.Roles is { Count: > 0 })
                {
                    var overrideCallerRoles = await GetCallerRolesAsync(role, requestContext, cancellationToken);
                    if (!overrideCallerRoles.IsSuccess)
                        return Result<AuthorizeOutput>.Fail(overrideCallerRoles.Error);
                    var overrideAllowed = await EvaluateWithGrantsAsync(overrideCallerRoles.Value, transitionOverride.Roles!, instance, requestContext, cancellationToken);
                    logger.AuthorizeRequest(domain, workflow, Describe(overrideCallerRoles.Value), overrideAllowed);
                    return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = overrideAllowed });
                }
            }
            // checkQueryRoles: check state override first, then forward
            else if (checkQueryRoles)
            {
                var subFlowCurrentState = subflow.SubFlowCurrentState;
                if (subFlowConfig?.HasQueryRoleOverrides == true &&
                    !string.IsNullOrWhiteSpace(subFlowCurrentState) &&
                    subFlowConfig.Overrides!.States!.TryGetValue(subFlowCurrentState, out var stateOverride) &&
                    stateOverride.QueryRoles is { Count: > 0 })
                {
                    var overrideCallerRoles = await GetCallerRolesAsync(role, requestContext, cancellationToken);
                    if (!overrideCallerRoles.IsSuccess)
                        return Result<AuthorizeOutput>.Fail(overrideCallerRoles.Error);
                    var overrideAllowed = await EvaluateWithGrantsAsync(overrideCallerRoles.Value, stateOverride.QueryRoles!, instance, requestContext, cancellationToken);
                    logger.AuthorizeRequest(domain, workflow, Describe(overrideCallerRoles.Value), overrideAllowed);
                    return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = overrideAllowed });
                }
            }

            // Forward to SubFlow (functionKey, no-override transition, no-override queryRoles)
            var resolvedForForward = await GetCallerRolesAsync(role, requestContext, cancellationToken);
            if (!resolvedForForward.IsSuccess)
                return Result<AuthorizeOutput>.Fail(resolvedForForward.Error);
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
                requestContext,
                cancellationToken);
        }

        var callerRolesResult = await GetCallerRolesAsync(role, requestContext, cancellationToken);
        if (!callerRolesResult.IsSuccess)
            return Result<AuthorizeOutput>.Fail(callerRolesResult.Error);
        var allowed = await EvaluateAuthorizeAsync(wf, callerRolesResult.Value, transitionKey, functionKey, instance, checkQueryRoles, domain, workflowVersion, requestContext, cancellationToken);
        logger.AuthorizeRequest(domain, workflow, Describe(callerRolesResult.Value), allowed);
        return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = allowed });
    }

    /// <summary>
    /// Resolves the caller's role set through the configured provider, falling back to the explicit
    /// <c>role</c> request parameter only when the provider reports no roles at all.
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

    private async Task<Result<IReadOnlyList<string>?>> GetCallerRolesAsync(
        string? roleParameter,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        var resolved = await callerRoleResolver.ResolveRolesAsync(requestContext?.Headers, cancellationToken);
        if (!resolved.IsSuccess)
            return Result<IReadOnlyList<string>?>.Fail(resolved.Error);

        if (resolved.Value is { Length: > 0 } roles)
            return Result<IReadOnlyList<string>?>.Ok(roles);

        return Result<IReadOnlyList<string>?>.Ok(
            string.IsNullOrWhiteSpace(roleParameter) ? null : [roleParameter.Trim()]);
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
    /// Validates instance-level authorize: exactly one of transitionKey, functionKey, or checkQueryRoles.
    /// </summary>
    private static Result<AuthorizeOutput>? ValidateAuthorizeTargetInstance(string? transitionKey, string? functionKey, bool checkQueryRoles)
    {
        var hasTransition = !string.IsNullOrWhiteSpace(transitionKey);
        var hasFunction = !string.IsNullOrWhiteSpace(functionKey);
        var count = (hasTransition ? 1 : 0) + (hasFunction ? 1 : 0) + (checkQueryRoles ? 1 : 0);
        if (count != 1)
            return Result<AuthorizeOutput>.Fail(WorkflowErrors.AuthorizeRequiresExactlyOneTarget());
        return null;
    }

    /// <summary>
    /// Evaluates authorize for a single target: transition, function, or state-based query roles.
    /// If any caller role is allowed, returns true. Caller validates exactly one target is specified.
    /// Resolves predefined instance roles ($InstanceStarter, $PreviousUser) when instance is present.
    /// </summary>
    private async Task<bool> EvaluateAuthorizeAsync(
        Definitions.Workflow workflow,
        IReadOnlyList<string>? callerRoles,
        string? transitionKey,
        string? functionKey,
        Instance? instance,
        bool checkQueryRoles,
        string domain,
        string? workflowVersion,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        if (callerRoles is null || callerRoles.Count == 0)
            return await EvaluateAuthorizeForSingleRoleAsync(workflow, null, transitionKey, functionKey, instance, checkQueryRoles, domain, workflowVersion, requestContext, cancellationToken);

        foreach (var role in callerRoles)
        {
            if (string.IsNullOrWhiteSpace(role))
                continue;
            var allowed = await EvaluateAuthorizeForSingleRoleAsync(workflow, role.Trim(), transitionKey, functionKey, instance, checkQueryRoles, domain, workflowVersion, requestContext, cancellationToken);
            if (allowed)
                return true;
        }
        return false;
    }

    private async Task<bool> EvaluateAuthorizeForSingleRoleAsync(
        Definitions.Workflow workflow,
        string? role,
        string? transitionKey,
        string? functionKey,
        Instance? instance,
        bool checkQueryRoles,
        string domain,
        string? workflowVersion,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        if (checkQueryRoles)
            return await EvaluateQueryRolesAsync(workflow, role, instance, requestContext, cancellationToken);

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
                role,
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
            return await transitionAuthorizationManager.IsRoleAllowedForGrantsAsync(role, fnResult.Value!.Roles, instance, requestContext, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Evaluates a list of role grants against the caller roles (multi-role: any allowed → allow).
    /// Used to apply SubFlow override grants locally without forwarding to the SubFlow.
    /// </summary>
    private async Task<bool> EvaluateWithGrantsAsync(
        IReadOnlyList<string>? callerRoles,
        IReadOnlyCollection<RoleGrant> grants,
        Instance instance,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        if (callerRoles is null || callerRoles.Count == 0)
            return false;
        foreach (var r in callerRoles)
        {
            if (string.IsNullOrWhiteSpace(r))
                continue;
            if (await transitionAuthorizationManager.IsRoleAllowedForGrantsAsync(r, grants, instance, requestContext, cancellationToken))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Evaluates state-based query roles: instance effective state → state queryRoles or workflow root queryRoles.
    /// Resolves predefined instance roles ($InstanceStarter, $PreviousUser) when instance is present. DENY wins, else ALLOW.
    /// </summary>
    private async Task<bool> EvaluateQueryRolesAsync(
        Definitions.Workflow workflow,
        string? role,
        Instance? instance,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        if (instance == null)
            return false;
        return await transitionAuthorizationManager.IsQueryAllowedAsync(
            workflow, instance, role is null ? null : [role], requestContext, cancellationToken);
    }
}
