using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.Authorization.DTOs;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Monitor.Authorization;

/// <inheritdoc cref="IMonitorAuthorizationQueryService" />
public sealed class MonitorAuthorizationQueryService(
    IServiceProvider serviceProvider,
    IInstanceRepository instanceRepository,
    IComponentCacheStore componentCacheStore)
    : ApplicationService(serviceProvider), IMonitorAuthorizationQueryService
{
    /// <inheritdoc />
    public async Task<Result<MonitorAuthorizationMatrixResponse>> GetWorkflowMatrixAsync(
        MonitorGetWorkflowPermissionsInput input,
        CancellationToken cancellationToken = default)
    {
        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, input.Workflow, input.Version, cancellationToken);

        if (!flowResult.IsSuccess || flowResult.Value is not { } flow)
            return Result<MonitorAuthorizationMatrixResponse>.Fail(
                Error.NotFound("workflow.notFound", $"Workflow '{input.Workflow}' definition not found."));

        var matrix = await BuildMatrixAsync(flow, input.Domain, cancellationToken);

        var roles = CollectRoles(input.Role, input.QueryRoles);
        if (roles.Count > 0)
            matrix.Authorize = ComputeWorkflowAuthorize(flow, roles, input.TransitionKey, currentState: null);

        return Result<MonitorAuthorizationMatrixResponse>.Ok(matrix);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorAuthorizationMatrixResponse>> GetInstanceMatrixAsync(
        MonitorGetInstancePermissionsInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);

        if (instance is null)
            return Result<MonitorAuthorizationMatrixResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        if (string.IsNullOrEmpty(instance.Flow))
            return Result<MonitorAuthorizationMatrixResponse>.Fail(
                Error.Validation("instance.invalidFlow", $"Instance '{input.Instance}' has no workflow reference."));

        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, instance.Flow, instance.FlowVersion, cancellationToken);

        if (!flowResult.IsSuccess || flowResult.Value is not { } flow)
            return Result<MonitorAuthorizationMatrixResponse>.Fail(
                Error.NotFound("workflow.notFound", $"Workflow '{instance.Flow}' definition not found."));

        var matrix = await BuildMatrixAsync(flow, input.Domain, cancellationToken);

        var roles = CollectRoles(input.Role, input.QueryRoles);
        if (roles.Count > 0)
            matrix.Authorize = ComputeWorkflowAuthorize(flow, roles, input.TransitionKey, instance.CurrentState);

        return Result<MonitorAuthorizationMatrixResponse>.Ok(matrix);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorTransitionPermissionsResponse>> GetTransitionPermissionsAsync(
        MonitorGetWorkflowPermissionsInput input,
        CancellationToken cancellationToken = default)
    {
        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, input.Workflow, input.Version, cancellationToken);

        if (!flowResult.IsSuccess || flowResult.Value is not { } flow)
            return Result<MonitorTransitionPermissionsResponse>.Fail(
                Error.NotFound("workflow.notFound", $"Workflow '{input.Workflow}' definition not found."));

        return Result<MonitorTransitionPermissionsResponse>.Ok(
            new MonitorTransitionPermissionsResponse
            {
                Transitions = AuthorizationMatrixMapper.MapTransitions(flow)
            });
    }

    /// <inheritdoc />
    public async Task<Result<MonitorFunctionPermissionsResponse>> GetFunctionPermissionsAsync(
        MonitorGetWorkflowPermissionsInput input,
        CancellationToken cancellationToken = default)
    {
        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, input.Workflow, input.Version, cancellationToken);

        if (!flowResult.IsSuccess || flowResult.Value is not { } flow)
            return Result<MonitorFunctionPermissionsResponse>.Fail(
                Error.NotFound("workflow.notFound", $"Workflow '{input.Workflow}' definition not found."));

        return Result<MonitorFunctionPermissionsResponse>.Ok(
            new MonitorFunctionPermissionsResponse
            {
                Functions = await MapFunctionsAsync(input.Domain, flow, cancellationToken)
            });
    }

    private async Task<MonitorAuthorizationMatrixResponse> BuildMatrixAsync(
        WorkflowDefinition flow, string domain, CancellationToken ct)
        => new()
        {
            WorkflowKey = flow.Key,
            Version = flow.Version,
            QueryRoles = AuthorizationMatrixMapper.Map(flow.QueryRoles),
            States = AuthorizationMatrixMapper.MapStates(flow),
            Transitions = AuthorizationMatrixMapper.MapTransitions(flow),
            Functions = await MapFunctionsAsync(domain, flow, ct)
        };

    private static MonitorAuthorizeResult ComputeWorkflowAuthorize(
        WorkflowDefinition flow, List<string> roles, string? transitionKey, string? currentState)
    {
        var allFlowTransitions = flow.States.SelectMany(s => s.Transitions).Concat(flow.SharedTransitions);

        IEnumerable<Transition> candidates;
        if (!string.IsNullOrWhiteSpace(transitionKey))
        {
            candidates = allFlowTransitions.Where(t =>
                string.Equals(t.Key, transitionKey, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(currentState))
        {
            candidates = allFlowTransitions.Where(t =>
                string.Equals(t.From, currentState, StringComparison.OrdinalIgnoreCase)
                || (t.From is null && (t.AvailableIn.Count == 0
                    || t.AvailableIn.Any(s => string.Equals(s, currentState, StringComparison.OrdinalIgnoreCase)))));
        }
        else
        {
            candidates = allFlowTransitions;
        }

        var allGrants = candidates.SelectMany(t => AuthorizationMatrixMapper.Map(t.Roles)).ToList();
        var allowed = AuthorizationMatrixMapper.IsAllowed(allGrants, roles);

        var roleSet = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
        var matchedRoles = allGrants
            .Where(g => roleSet.Contains(g.Role)
                && string.Equals(g.Grant, "allow", StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Role)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MonitorAuthorizeResult { Allowed = allowed, MatchedRoles = matchedRoles };
    }

    private async Task<List<MonitorFunctionPermission>> MapFunctionsAsync(
        string domain, WorkflowDefinition flow, CancellationToken ct)
    {
        var refs = flow.Functions.ToList();
        var lookupTasks = refs
            .Select(fref => componentCacheStore.GetFunctionAsync(domain, fref.Key, fref.Version, ct))
            .ToList();
        var results = await Task.WhenAll(lookupTasks);

        return refs.Zip(results, (fref, fnResult) => new MonitorFunctionPermission
        {
            Key = fref.Key,
            Roles = fnResult.IsSuccess && fnResult.Value is { } fn
                ? AuthorizationMatrixMapper.Map(fn.Roles)
                : []
        }).ToList();
    }

    private static List<string> CollectRoles(string? role, List<string> queryRoles)
    {
        var roles = new List<string>(queryRoles);
        if (!string.IsNullOrWhiteSpace(role)) roles.Add(role);
        return roles;
    }
}
