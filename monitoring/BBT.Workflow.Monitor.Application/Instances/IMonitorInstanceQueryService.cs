using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.Instances.DTOs;

namespace BBT.Workflow.Monitor.Instances;

/// <summary>
/// Provides monitor-specific aggregate queries over workflow instances.
/// This service is read-only and returns enriched projections optimised for the vnext-forge monitoring UI.
/// All queries use non-tracking EF Core reads for optimal performance.
/// </summary>
public interface IMonitorInstanceQueryService
{
    /// <summary>
    /// Returns instance metadata and active correlation info.
    /// Extension-free — no script evaluation is performed.
    /// </summary>
    Task<Result<MonitorInstanceResponse>> GetInstanceAsync(
        MonitorGetInstanceInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paged, filterable list of instances using GraphQL filter syntax.
    /// </summary>
    Task<Result<InstanceListWithGroupsResponse<MonitorInstanceResponse>>> GetInstancesAsync(
        MonitorGetInstancesInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest instance data plus full version history so the client can
    /// visualise how attributes changed over time.
    /// Extension-free — no script evaluation is performed.
    /// </summary>
    Task<Result<MonitorInstanceDataResponse>> GetInstanceDataAsync(
        MonitorGetInstanceDataInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the ordered list of InstanceTransition records for a given instance,
    /// enabling the client to render the state-transition flow graph.
    /// </summary>
    Task<Result<MonitorInstanceHistoryResponse>> GetInstanceHistoryAsync(
        MonitorGetInstanceHistoryInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the task execution records for a specific transition.
    /// Kept as a separate, on-demand endpoint to avoid N+1 when the client only needs
    /// the transition flow (GetInstanceHistory).
    /// </summary>
    Task<Result<List<MonitorInstanceTaskResponse>>> GetInstanceTaskAsync(
        MonitorGetInstanceTaskInput input,
        CancellationToken cancellationToken = default);
}
