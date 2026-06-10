using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.Stats.DTOs;

namespace BBT.Workflow.Monitor.Stats;

/// <inheritdoc />
public sealed class MonitorStatsService(
    IServiceProvider serviceProvider,
    IInstanceRepository instanceRepository,
    IComponentCacheStore componentCacheStore)
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
            var response = new MonitorInstanceCountersResponse();

            foreach (var status in StatusNames)
            {
                var count = await CountByStatusAsync(status, ct);
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

    private async Task<long> CountByStatusAsync(string statusName, CancellationToken ct)
    {
        var filter = "{\"status\":{\"eq\":\"" + statusName + "\"}}";
        return await instanceRepository.CountAsync(filter, ct);
    }
}
