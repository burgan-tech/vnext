using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.Stats.DTOs;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Monitor.Stats;

/// <inheritdoc />
public sealed class MonitorStatsService(
    IServiceProvider serviceProvider,
    IInstanceRepository instanceRepository,
    IInstanceTaskRepository taskRepository,
    IInstanceTransitionRepository transitionRepository,
    IComponentCacheStore componentCacheStore,
    IDomainCacheContext domainCacheContext,
    IServiceScopeFactory serviceScopeFactory)
    : ApplicationService(serviceProvider), IMonitorStatsService
{
    private static readonly string[] StatusNames = ["Active", "Busy", "Completed", "Faulted", "Passive"];

    /// <inheritdoc />
    public async Task<Result<MonitorInstanceCountersResponse>> GetInstanceCountersAsync(
        MonitorGetInstanceCountersInput input,
        CancellationToken cancellationToken = default)
    {
        return await ResultExtensions.TryAsync(async ct =>
        {
            if (string.IsNullOrWhiteSpace(input.Workflow))
                return await CountAcrossDomainAsync(input.Domain, ct);

            return await CountInCurrentSchemaAsync(instanceRepository, ct);
        }, cancellationToken);
    }

    // PERF NOTE: this runs ~4×stateCount COUNT queries; acceptable for Phase 1, revisit if a workflow has very many states.
    /// <inheritdoc />
    public async Task<Result<MonitorStateDistributionResponse>> GetStateDistributionAsync(
        MonitorGetStateDistributionInput input,
        CancellationToken cancellationToken = default)
    {
        var flowResult = await componentCacheStore.GetFlowAsync(
            input.Domain, input.Workflow, null, cancellationToken);
        if (!flowResult.IsSuccess || flowResult.Value is null)
            return Result<MonitorStateDistributionResponse>.Fail(
                Error.NotFound("workflow.notFound",
                    $"Workflow '{input.Workflow}' not found in domain '{input.Domain}'."));

        return await ResultExtensions.TryAsync(async ct =>
        {
            var response = new MonitorStateDistributionResponse();
            foreach (var state in flowResult.Value!.States)
            {
                var key = state.Key;
                var total   = await instanceRepository.CountAsync("{\"currentState\":{\"eq\":\"" + key + "\"}}", ct);
                var active  = await instanceRepository.CountAsync("{\"and\":[{\"currentState\":{\"eq\":\"" + key + "\"}},{\"status\":{\"eq\":\"Active\"}}]}", ct);
                var busy    = await instanceRepository.CountAsync("{\"and\":[{\"currentState\":{\"eq\":\"" + key + "\"}},{\"status\":{\"eq\":\"Busy\"}}]}", ct);
                var faulted = await instanceRepository.CountAsync("{\"and\":[{\"currentState\":{\"eq\":\"" + key + "\"}},{\"status\":{\"eq\":\"Faulted\"}}]}", ct);

                response.States.Add(new MonitorStateCount
                {
                    StateKey = key, Total = total, Active = active, Busy = busy, Faulted = faulted
                });
                response.TotalActiveInstances += active;
            }
            return response;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorFaultStatsResponse>> GetFaultStatsAsync(
        MonitorGetWorkflowStatsInput input, CancellationToken cancellationToken = default)
    {
        return await ResultExtensions.TryAsync(async ct =>
        {
            var totalFaulted = await instanceRepository.CountAsync("{\"status\":{\"eq\":\"Faulted\"}}", ct);
            var byState = (await instanceRepository.GetFaultStateCountsAsync(ct))
                .Select(s => new MonitorKeyCount { Key = s.StateKey, Count = s.Count }).ToList();
            var byTask = (await taskRepository.GetTaskStatsAsync(ct))
                .Where(t => t.FailureCount > 0)
                .Select(t => new MonitorKeyCount { Key = t.TaskKey, Count = t.FailureCount }).ToList();

            var now = DateTime.UtcNow;
            var last1h = await instanceRepository.CountAsync(
                "{\"and\":[{\"status\":{\"eq\":\"Faulted\"}},{\"modifiedAt\":{\"gt\":\"" +
                now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"}}]}", ct);
            var last24h = await instanceRepository.CountAsync(
                "{\"and\":[{\"status\":{\"eq\":\"Faulted\"}},{\"modifiedAt\":{\"gt\":\"" +
                now.AddHours(-24).ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"}}]}", ct);
            var last7d = await instanceRepository.CountAsync(
                "{\"and\":[{\"status\":{\"eq\":\"Faulted\"}},{\"modifiedAt\":{\"gt\":\"" +
                now.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"}}]}", ct);

            return new MonitorFaultStatsResponse
            {
                TotalFaulted = totalFaulted,
                ByState = byState,
                ByTask = byTask,
                Trend = new MonitorTrend { Last1h = last1h, Last24h = last24h, Last7d = last7d }
            };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorTaskStatsResponse>> GetTaskStatsAsync(
        MonitorGetWorkflowStatsInput input, CancellationToken cancellationToken = default)
    {
        return await ResultExtensions.TryAsync(async ct =>
        {
            var stats = await taskRepository.GetTaskStatsAsync(ct);
            var items = stats.Select(s => new MonitorTaskStatItem
            {
                TaskKey = s.TaskKey,
                ExecutionCount = s.ExecutionCount,
                SuccessRate = StatsRateCalculator.Rate(s.SuccessCount, s.ExecutionCount),
                FailureRate = StatsRateCalculator.Rate(s.FailureCount, s.ExecutionCount)
            }).ToList();

            return new MonitorTaskStatsResponse
            {
                ByTask = items
            };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorDurationStatsResponse>> GetDurationStatsAsync(
        MonitorGetWorkflowStatsInput input, CancellationToken cancellationToken = default)
    {
        return await ResultExtensions.TryAsync(async ct =>
        {
            var d = await instanceRepository.GetDurationStatAsync(ct);
            return new MonitorDurationStatsResponse
            {
                AvgMs = d.AvgMs, MinMs = d.MinMs, MaxMs = d.MaxMs, CompletedCount = d.CompletedCount
            };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorTransitionStatsResponse>> GetTransitionStatsAsync(
        MonitorGetWorkflowStatsInput input, CancellationToken cancellationToken = default)
    {
        return await ResultExtensions.TryAsync(async ct =>
        {
            var stats = await transitionRepository.GetTransitionStatsAsync(ct);
            return new MonitorTransitionStatsResponse
            {
                ByTransition = stats
                    .GroupBy(s => s.TransitionKey)
                    .Select(g => new MonitorTransitionStatItem
                    {
                        TransitionKey = g.Key,
                        Count = g.Sum(x => x.Count),
                        CompletionRate = StatsRateCalculator.Rate(g.Sum(x => x.CompletedCount), g.Sum(x => x.Count)),
                        TriggerTypeBreakdown = new MonitorTriggerBreakdown
                        {
                            Manual = g.Sum(x => x.ManualCount),
                            Automatic = g.Sum(x => x.AutomaticCount),
                            Scheduled = g.Sum(x => x.ScheduledCount),
                            Event = g.Sum(x => x.EventCount)
                        }
                    }).ToList(),
                FlowDensity = stats.Select(s => new MonitorFlowDensity
                {
                    FromState = s.FromState, ToState = s.ToState, Count = s.Count
                }).ToList()
            };
        }, cancellationToken);
    }

    private async Task<MonitorInstanceCountersResponse> CountAcrossDomainAsync(
        string domain, CancellationToken ct)
    {
        var workflowKeys = await GetWorkflowKeysForDomainAsync(domain, ct);
        if (workflowKeys.Count == 0)
            return new MonitorInstanceCountersResponse();

        var perSchema = await Task.WhenAll(workflowKeys.Select(key => CountInIsolatedSchemaAsync(key, ct)));

        var response = new MonitorInstanceCountersResponse();
        foreach (var r in perSchema)
        {
            response.Active    += r.Active;
            response.Busy      += r.Busy;
            response.Completed += r.Completed;
            response.Faulted   += r.Faulted;
            response.Passive   += r.Passive;
            response.Total     += r.Total;
        }
        return response;
    }

    private async Task<MonitorInstanceCountersResponse> CountInIsolatedSchemaAsync(
        string schemaKey, CancellationToken ct)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var repo = scope.ServiceProvider.GetRequiredService<IInstanceRepository>();

        using (currentSchema.Use(schemaKey))
            return await CountInCurrentSchemaAsync(repo, ct);
    }

    private static async Task<MonitorInstanceCountersResponse> CountInCurrentSchemaAsync(
        IInstanceRepository repo, CancellationToken ct)
    {
        var response = new MonitorInstanceCountersResponse();
        foreach (var status in StatusNames)
        {
            var count = await repo.CountAsync("{\"status\":{\"eq\":\"" + status + "\"}}", ct);
            switch (status)
            {
                case "Active":    response.Active    = count; break;
                case "Busy":      response.Busy      = count; break;
                case "Completed": response.Completed = count; break;
                case "Faulted":   response.Faulted   = count; break;
                case "Passive":   response.Passive   = count; break;
            }
            response.Total += count;
        }
        return response;
    }

    private async Task<IReadOnlyList<string>> GetWorkflowKeysForDomainAsync(
        string domain, CancellationToken ct)
    {
        var snapResult = await domainCacheContext.Workflows.GetAllByDomainAsync(domain, ct);
        if (snapResult.IsSuccess && snapResult.Value is { Count: > 0 })
        {
            return snapResult.Value
                .Where(w => !string.IsNullOrWhiteSpace(w.Key))
                .Select(w => w.Key!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();
        var fromDb = (await runtimeService.GetAsync<Definitions.Workflow>(ct)).ToList();
        return fromDb
            .Where(w => w is not null
                        && !string.IsNullOrWhiteSpace(w.Key)
                        && string.Equals(w.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}
