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
    Task<Result<MonitorInstanceTimelineResponse>> GetInstanceTimelineAsync(
        MonitorGetInstanceTimelineInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the instance's current state plus the transitions available from it (definition-derived, no rule eval).</summary>
    Task<Result<MonitorInstanceStateResponse>> GetInstanceStateAsync(
        MonitorGetInstanceStateInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns root-cause fault detail for a faulted instance (failed tasks + unfinished transition).</summary>
    Task<Result<MonitorInstanceFaultResponse>> GetInstanceFaultsAsync(
        MonitorGetInstanceFaultsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the field-level diff between two data versions of an instance.</summary>
    Task<Result<MonitorInstanceDataDiffResponse>> GetInstanceDataDiffAsync(
        MonitorGetInstanceDataDiffInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the recursive sub-flow/sub-process hierarchy tree for an instance (cross-schema).</summary>
    Task<Result<MonitorHierarchyNode>> GetInstanceHierarchyAsync(
        MonitorGetInstanceHierarchyInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the view definition bound to the instance's current state or a given transition (P1).
    /// Returns null value (204) when no view is defined for the state or transition.
    /// </summary>
    Task<Result<MonitorInstanceViewResponse?>> GetInstanceViewAsync(
        MonitorGetInstanceViewInput input,
        CancellationToken cancellationToken = default);
}
