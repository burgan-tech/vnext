using BBT.Aether.Results;
using BBT.Workflow.Monitor.Authorization.DTOs;

namespace BBT.Workflow.Monitor.Authorization;

/// <summary>Read-only, definition-derived authorization queries.</summary>
public interface IMonitorAuthorizationQueryService
{
    /// <summary>
    /// Returns the workflow authorization matrix.
    /// When <see cref="MonitorGetWorkflowPermissionsInput.Role"/> is supplied, only entries
    /// where that role appears are returned; otherwise the full matrix is returned.
    /// </summary>
    Task<Result<MonitorAuthorizationMatrixResponse>> GetWorkflowMatrixAsync(
        MonitorGetWorkflowPermissionsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the instance-scoped permissions view: workflow-level roles, current state roles,
    /// transitions available from the current state, and workflow functions.
    /// </summary>
    Task<Result<MonitorInstancePermissionsResponse>> GetInstancePermissionsAsync(
        MonitorGetInstancePermissionsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns transition-level permissions sub-view.</summary>
    Task<Result<MonitorTransitionPermissionsResponse>> GetTransitionPermissionsAsync(
        MonitorGetWorkflowPermissionsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns function-level permissions sub-view.</summary>
    Task<Result<MonitorFunctionPermissionsResponse>> GetFunctionPermissionsAsync(
        MonitorGetWorkflowPermissionsInput input,
        CancellationToken cancellationToken = default);
}
