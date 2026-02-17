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
/// When instance has active subflow, forwards authorize request to subflow via IAuthorizeGateway.
/// </summary>
public sealed class AuthorizeAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IAuthorizeGateway authorizeGateway,
    ILogger<AuthorizeAppService> logger) : ApplicationService(serviceProvider), IAuthorizeAppService
{
    /// <inheritdoc />
    public async Task<Result<AuthorizeOutput>> GetAuthorizeResultAsync(
        string domain,
        string workflow,
        string role,
        string? transitionKey,
        string? functionKey,
        string? version = null,
        bool checkQueryRoles = false,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateAuthorizeTargetWorkflow(transitionKey, functionKey, checkQueryRoles);
        if (validation.HasValue)
            return validation.Value;

        runtimeInfoProvider.Check(domain);
        var workflowVersion = NormalizeVersion(version);
        var workflowResult = await componentCacheStore.GetFlowAsync(domain, workflow, workflowVersion, cancellationToken);
        if (!workflowResult.IsSuccess)
            return Result<AuthorizeOutput>.Fail(workflowResult.Error);

        var wf = workflowResult.Value!;
        var allowed = await EvaluateAuthorizeAsync(wf, role, transitionKey, functionKey, null, checkQueryRoles, componentCacheStore, domain, workflowVersion, cancellationToken);
        logger.AuthorizeRequest(domain, workflow, role, allowed);
        return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = allowed });
    }

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

        // When instance has active subflow, forward authorize to subflow instance (local or remote via gateway)
        if (instance?.Subflow != null)
        {
            var subflow = instance.Subflow;
            return await authorizeGateway.GetAuthorizeResultForInstanceAsync(
                subflow.SubFlowDomain,
                subflow.SubFlowName,
                subflow.SubFlowInstanceId.ToString(),
                role,
                transitionKey,
                functionKey,
                version,
                checkQueryRoles,
                cancellationToken);
        }

        var allowed = await EvaluateAuthorizeAsync(wf, role, transitionKey, functionKey, instance, checkQueryRoles, componentCacheStore, domain, workflowVersion, cancellationToken);
        logger.AuthorizeRequest(domain, workflow, role, allowed);
        return Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = allowed });
    }

    /// <inheritdoc />
    public async Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixAsync(
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

        Instance? instance = null;
        var instanceResult = await instanceRepository.FindByIdentifierAsync(instanceId, cancellationToken);
        if (instanceResult is not null)
            instance = instanceResult;

        if (instance?.Subflow != null)
        {
            var subflow = instance.Subflow;
            return await authorizeGateway.GetAuthorizationMatrixForInstanceAsync(
                subflow.SubFlowDomain,
                subflow.SubFlowName,
                subflow.SubFlowInstanceId.ToString(),
                version,
                cancellationToken);
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
                Roles = ToRoleGrantDtos(t.Roles)
            });
    }

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
    /// Validates workflow-level authorize: exactly one of transitionKey or functionKey; checkQueryRoles true → error.
    /// </summary>
    private static Result<AuthorizeOutput>? ValidateAuthorizeTargetWorkflow(string? transitionKey, string? functionKey, bool checkQueryRoles)
    {
        if (checkQueryRoles)
            return Result<AuthorizeOutput>.Fail(WorkflowErrors.AuthorizeQueryRolesRequiresInstance());
        var hasTransition = !string.IsNullOrWhiteSpace(transitionKey);
        var hasFunction = !string.IsNullOrWhiteSpace(functionKey);
        if (hasTransition == hasFunction)
            return Result<AuthorizeOutput>.Fail(WorkflowErrors.AuthorizeRequiresExactlyOneTarget());
        return null;
    }

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
    /// Caller validates exactly one target is specified.
    /// </summary>
    private static async Task<bool> EvaluateAuthorizeAsync(
        Definitions.Workflow workflow,
        string role,
        string? transitionKey,
        string? functionKey,
        Instance? instance,
        bool checkQueryRoles,
        IComponentCacheStore componentCacheStore,
        string domain,
        string? workflowVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(role))
            return false;

        if (checkQueryRoles)
            return EvaluateQueryRoles(workflow, role, instance);

        if (!string.IsNullOrWhiteSpace(transitionKey))
        {
            var transition = workflow.FindTransitionInContext(transitionKey);
            return transition != null && EvaluateRoles(role, transition.Roles);
        }

        if (!string.IsNullOrWhiteSpace(functionKey))
        {
            var fnResult = await componentCacheStore.GetFunctionAsync(domain, functionKey, workflowVersion, cancellationToken);
            return fnResult.IsSuccess && EvaluateRoles(role, fnResult.Value!.Roles);
        }

        return false;
    }

    /// <summary>
    /// Evaluates state-based query roles: instance current state → state queryRoles or workflow root queryRoles; DENY wins, else ALLOW.
    /// </summary>
    private static bool EvaluateQueryRoles(Definitions.Workflow workflow, string role, Instance? instance)
    {
        if (instance == null)
            return false;
        var currentStateKey = instance.GetCurrentState;
        if (string.IsNullOrWhiteSpace(currentStateKey))
            return false;
        var state = workflow.FindState(currentStateKey);
        var queryRoles = state != null && state.QueryRoles.Count > 0 ? state.QueryRoles : workflow.QueryRoles;
        return EvaluateRoles(role, queryRoles);
    }

    /// <summary>
    /// Evaluates role against role grants. DENY always wins; else any ALLOW match → true; else false.
    /// </summary>
    private static bool EvaluateRoles(string role, IReadOnlyCollection<RoleGrant> roleGrants)
    {
        if (roleGrants.Count == 0)
            return false;
        var normalizedRole = role.Trim();
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
        return false;
    }
}
