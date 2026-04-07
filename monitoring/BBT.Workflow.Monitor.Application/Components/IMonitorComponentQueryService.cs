using BBT.Aether.Results;
using BBT.Workflow.Monitor.Components.DTOs;

namespace BBT.Workflow.Monitor.Components;

/// <summary>
/// Provides read-only queries over workflow component definitions (flows, tasks, schemas,
/// extensions, functions, views).  The client specifies the desired component type in the
/// request; a single service method dispatches to the appropriate cache-store lookup.
/// </summary>
public interface IMonitorComponentQueryService
{
    /// <summary>
    /// Returns component definitions for the given type and domain.
    /// When <c>Key</c> is set on the input, returns the matching single component.
    /// When <c>Key</c> is omitted, returns all cached components of that type.
    /// </summary>
    Task<Result<MonitorComponentResponse>> GetComponentsAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default);
}
