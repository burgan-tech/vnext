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

        if (!string.IsNullOrWhiteSpace(input.Role))
            return Result<MonitorAuthorizationMatrixResponse>.Ok(
                AuthorizationMatrixMapper.FilterByRole(matrix, input.Role));

        return Result<MonitorAuthorizationMatrixResponse>.Ok(matrix);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorInstancePermissionsResponse>> GetInstancePermissionsAsync(
        MonitorGetInstancePermissionsInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            input.Instance, cancellationToken);

        if (instance is null)
            return Result<MonitorInstancePermissionsResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        if (string.IsNullOrEmpty(instance.Flow))
            return Result<MonitorInstancePermissionsResponse>.Fail(
                Error.Validation("instance.invalidFlow", $"Instance '{input.Instance}' has no workflow reference."));

        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, instance.Flow, instance.FlowVersion, cancellationToken);

        if (!flowResult.IsSuccess || flowResult.Value is not { } flow)
            return Result<MonitorInstancePermissionsResponse>.Fail(
                Error.NotFound("workflow.notFound", $"Workflow '{instance.Flow}' definition not found."));

        var currentState = flow.FindState(instance.CurrentState ?? string.Empty);

        var stateTransitions = currentState?.Transitions ?? [];
        var availableShared = flow.SharedTransitions.Where(t =>
            t.AvailableIn.Count == 0
            || t.AvailableIn.Any(s => string.Equals(s, instance.CurrentState, StringComparison.OrdinalIgnoreCase)));

        var transitions = stateTransitions.Concat(availableShared)
            .Select(t => new MonitorTransitionPermission
            {
                Key = t.Key,
                From = t.From,
                Target = t.Target,
                Roles = AuthorizationMatrixMapper.Map(t.Roles)
            })
            .ToList();

        var response = new MonitorInstancePermissionsResponse
        {
            WorkflowKey = flow.Key,
            Version = flow.Version,
            QueryRoles = AuthorizationMatrixMapper.Map(flow.QueryRoles),
            State = currentState is not null
                ? new MonitorStatePermission
                {
                    Key = currentState.Key,
                    QueryRoles = AuthorizationMatrixMapper.Map(currentState.QueryRoles)
                }
                : null,
            Transitions = transitions,
            Functions = await MapFunctionsAsync(input.Domain, flow, cancellationToken)
        };

        if (!string.IsNullOrWhiteSpace(input.Role))
            return Result<MonitorInstancePermissionsResponse>.Ok(
                AuthorizationMatrixMapper.FilterByRole(response, input.Role));

        return Result<MonitorInstancePermissionsResponse>.Ok(response);
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

}
