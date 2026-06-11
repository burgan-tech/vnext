using BBT.Aether.Results;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Faults a child subflow instance on request from its parent. Encapsulates the load + terminal
/// guard + <c>Fault</c> logic so it is reusable across consumers (the Orchestration internal
/// endpoint that the Inbox forwards to, hooks, etc.).
/// </summary>
public interface IChildSubflowFaultService
{
    /// <summary>
    /// Loads the child instance and faults it (idempotent: no-op if absent or already terminal).
    /// Runs under the ambient unit of work / current schema established by the caller.
    /// </summary>
    Task<Result> FaultChildAsync(
        Guid instanceId,
        string domain,
        string flow,
        Guid parentInstanceId,
        CancellationToken cancellationToken = default);
}
