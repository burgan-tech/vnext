using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Monitor.Common.DTOs;
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
    /// When <c>Key</c> is set, returns that single component (no pagination metadata).
    /// When <c>Key</c> is omitted, returns a paged list via <c>Page</c>/<c>PageSize</c> from the input.
    /// </summary>
    Task<Result<MonitorPagedResponse<JsonElement>>> GetComponentsAsync(
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
    /// Returns a lightweight summary list (key, version, domain, labels) for the given
    /// component type and domain — without the full definition payload.
    /// Snapshot first; falls back to runtime DB load and cache warm if snapshot is empty.
    /// </summary>
    Task<Result<MonitorPagedResponse<MonitorComponentSummaryItem>>> GetComponentSummaryAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns detail for a single component identified by <c>key</c> (and optional <c>version</c>).
    /// Includes the component's <c>flow</c> identifier and all published versions sorted descending.
    /// Returns 404 when the component is not found.
    /// </summary>
    Task<Result<MonitorComponentDetailResponse>> GetComponentDetailAsync(
        MonitorGetComponentsInput input,
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
