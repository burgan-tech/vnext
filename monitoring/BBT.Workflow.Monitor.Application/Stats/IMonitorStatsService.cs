using BBT.Aether.Results;
using BBT.Workflow.Monitor.Stats.DTOs;

namespace BBT.Workflow.Monitor.Stats;

/// <summary>Read-only aggregation queries (status counters, state distribution) for monitor dashboards.</summary>
public interface IMonitorStatsService
{
    /// <summary>
    /// Returns the count of instances grouped by status for a specific workflow or the whole domain.
    /// </summary>
    /// <param name="input">Domain and optional workflow scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-status counters plus a total.</returns>
    Task<Result<MonitorInstanceCountersResponse>> GetInstanceCountersAsync(
        MonitorGetInstanceCountersInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a live count of instances in each workflow state, broken down by Active/Busy/Faulted status.
    /// The workflow definition is fetched from cache to enumerate the states.
    /// </summary>
    /// <param name="input">Domain and workflow key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-state instance counts plus an aggregate of total active instances.</returns>
    Task<Result<MonitorStateDistributionResponse>> GetStateDistributionAsync(
        MonitorGetStateDistributionInput input,
        CancellationToken cancellationToken = default);
}
