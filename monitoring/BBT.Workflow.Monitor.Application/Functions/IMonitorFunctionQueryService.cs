using BBT.Aether.Application;
using BBT.Aether.Results;
using BBT.Workflow.Monitor.Functions.DTOs;

namespace BBT.Workflow.Monitor.Functions;

/// <summary>
/// Read-only query service for function definitions, scoped to domain-level or instance-level context.
/// Functions are never executed; only their definitions (key, version, scope, roles, task count) are returned.
/// </summary>
public interface IMonitorFunctionQueryService : IApplicationService
{
    /// <summary>
    /// Returns all function definitions published for the given domain that have <c>Domain</c> scope
    /// (callable from any workflow without explicit registration).
    /// Loads from the runtime DB (sys_functions schema) and warms the component cache.
    /// </summary>
    /// <param name="input">Domain identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<MonitorFunctionListResponse>> GetDomainFunctionsAsync(
        MonitorGetDomainFunctionsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all function definitions explicitly registered in the workflow that the given instance
    /// is running, regardless of their scope.
    /// The instance's <c>FlowVersion</c> is used to pin the exact definition version so that
    /// the result reflects the workflow as it was when the instance was started.
    /// </summary>
    /// <param name="input">Domain, workflow, and instance identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<MonitorFunctionListResponse>> GetInstanceFunctionsAsync(
        MonitorGetInstanceFunctionsInput input,
        CancellationToken cancellationToken = default);
}
