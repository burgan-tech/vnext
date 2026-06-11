using BBT.Aether.Results;
using BBT.Workflow.Monitor.Components.DTOs;

namespace BBT.Workflow.Monitor.Components;

/// <summary>
/// Provides read-only queries over workflow component definitions (flows, tasks, schemas,
/// extensions, functions, views). The client specifies the desired component type in the
/// request; a single service method dispatches to the appropriate cache-store lookup.
/// </summary>
public interface IMonitorComponentQueryService
{
    /// <summary>
    /// Returns component definitions for the given type and domain.
    /// When <c>Key</c> is set, returns that component or 404.
    /// When <c>Key</c> is omitted, returns the full list: snapshot first, then runtime DB load and cache warm if empty.
    /// </summary>
    Task<Result<MonitorComponentResponse>> GetComponentsAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns per-type component counts (flows, tasks, schemas, views, functions, extensions)
    /// for the given domain. Snapshot first; falls back to runtime DB load and cache warm if snapshot is empty.
    /// </summary>
    Task<Result<MonitorComponentStatsResponse>> GetComponentStatsAsync(
        MonitorGetComponentStatsInput input,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns all component dependencies of a workflow definition (tasks, schemas, views,
    /// functions, extensions, sub-flows) with their reference site in the definition.
    /// </summary>
    Task<Result<MonitorDependencyResponse>> GetWorkflowDependenciesAsync(
        string domain, string workflow, string? version,
        CancellationToken cancellationToken = default);
}
