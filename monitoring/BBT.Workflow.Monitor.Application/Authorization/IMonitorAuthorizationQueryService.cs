using BBT.Aether.Results;
using BBT.Workflow.Monitor.Authorization.DTOs;

namespace BBT.Workflow.Monitor.Authorization;

/// <summary>Read-only, definition-derived authorization queries (P3, P4, P17, P19).</summary>
public interface IMonitorAuthorizationQueryService
{
    /// <summary>
    /// Returns the full workflow authorization matrix (P4, workflow-scoped).
    /// When <see cref="MonitorGetWorkflowPermissionsInput.Role"/> or <see cref="MonitorGetWorkflowPermissionsInput.QueryRoles"/>
    /// are supplied, the response also contains an inline <c>authorize</c> verdict.
    /// </summary>
    Task<Result<MonitorAuthorizationMatrixResponse>> GetWorkflowMatrixAsync(
        MonitorGetWorkflowPermissionsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the full workflow authorization matrix resolved via an instance (P4, instance-scoped).
    /// When <see cref="MonitorGetInstancePermissionsInput.Role"/> or <see cref="MonitorGetInstancePermissionsInput.QueryRoles"/>
    /// are supplied, the response also contains an inline <c>authorize</c> verdict based on the instance's current state.
    /// </summary>
    Task<Result<MonitorAuthorizationMatrixResponse>> GetInstanceMatrixAsync(
        MonitorGetInstancePermissionsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns transition-level permissions sub-view (P17).</summary>
    Task<Result<MonitorTransitionPermissionsResponse>> GetTransitionPermissionsAsync(
        MonitorGetWorkflowPermissionsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns function-level permissions sub-view (P19).</summary>
    Task<Result<MonitorFunctionPermissionsResponse>> GetFunctionPermissionsAsync(
        MonitorGetWorkflowPermissionsInput input,
        CancellationToken cancellationToken = default);
}
