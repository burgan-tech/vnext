using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.Functions.DTOs;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Monitor.Functions;

/// <summary>
/// Read-only service that surfaces function definitions for dashboard and observability queries.
/// No function is ever executed; this service only reads definitions from the component cache and DB.
/// </summary>
public sealed class MonitorFunctionQueryService(
    IServiceProvider serviceProvider,
    IInstanceRepository instanceRepository,
    IComponentCacheStore componentCacheStore,
    IServiceScopeFactory serviceScopeFactory,
    IRuntimeInfoProvider runtimeInfoProvider)
    : ApplicationService(serviceProvider), IMonitorFunctionQueryService
{
    /// <inheritdoc />
    public async Task<Result<MonitorFunctionListResponse>> GetDomainFunctionsAsync(
        MonitorGetDomainFunctionsInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        // Isolated scope: avoids leaking the request's workflow schema into sys_functions queries.
        await using var isolatedScope = serviceScopeFactory.CreateAsyncScope();
        var runtimeService = isolatedScope.ServiceProvider.GetRequiredService<IRuntimeService>();

        var all = (await runtimeService.GetAsync<Function>(cancellationToken))
            .Where(f => f is not null
                        && !string.IsNullOrWhiteSpace(f.Key)
                        && string.Equals(f.Domain, input.Domain, StringComparison.OrdinalIgnoreCase))
            .Cast<Function>()
            .ToList();

        // Keep only the latest version per key (same dedup strategy as MonitorComponentQueryService).
        var latest = all
            .GroupBy(f => f.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.Version, StringComparer.OrdinalIgnoreCase).First())
            .Where(f => f.Scope.Equals(TaskScope.Domain))
            .ToList();

        var items = latest.Select(MapToItem).ToList();
        return Result<MonitorFunctionListResponse>.Ok(new MonitorFunctionListResponse
        {
            Items = items,
            Total = items.Count
        });
    }

    /// <inheritdoc />
    public async Task<Result<MonitorFunctionListResponse>> GetInstanceFunctionsAsync(
        MonitorGetInstanceFunctionsInput input,
        CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindByIdentifierSlimAsync(
            input.Instance, cancellationToken);
        if (instance is null)
            return Result<MonitorFunctionListResponse>.Fail(
                Error.NotFound("instance.notFound", $"Instance '{input.Instance}' not found."));

        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, input.Workflow, instance.FlowVersion, cancellationToken);
        if (!flowResult.IsSuccess || flowResult.Value is not { } flow)
            return Result<MonitorFunctionListResponse>.Fail(
                Error.NotFound("workflow.notFound",
                    $"Workflow definition '{input.Workflow}' not found."));

        var items = new List<MonitorFunctionItem>();

        foreach (var fnRef in flow.Functions)
        {
            var fnResult = await componentCacheStore.GetFunctionAsync(
                input.Domain, fnRef.Key, fnRef.Version, cancellationToken);

            // Best-effort: skip functions whose definition is unavailable in cache/DB.
            if (!fnResult.IsSuccess || fnResult.Value is not { } fn)
                continue;

            items.Add(MapToItem(fn));
        }

        return Result<MonitorFunctionListResponse>.Ok(new MonitorFunctionListResponse
        {
            Items = items,
            Total = items.Count
        });
    }

    private static MonitorFunctionItem MapToItem(Function fn)
    {
        List<MonitorFunctionRoleItem>? roles = fn.Roles.Count > 0
            ? fn.Roles.Select(r => new MonitorFunctionRoleItem { Role = r.Role, Grant = r.Grant }).ToList()
            : null;

        return new MonitorFunctionItem
        {
            Key       = fn.Key,
            Version   = fn.Version,
            Scope     = fn.Scope.Description,
            TaskCount = fn.GetExecuteTasks().Count,
            Roles     = roles
        };
    }
}
