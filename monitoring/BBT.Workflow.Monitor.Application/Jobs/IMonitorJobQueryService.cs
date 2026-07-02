using BBT.Aether.Results;
using BBT.Workflow.Monitor.Common.DTOs;
using BBT.Workflow.Monitor.Jobs.DTOs;

namespace BBT.Workflow.Monitor.Jobs;

/// <summary>Read-only active job/timer queries (P7).</summary>
public interface IMonitorJobQueryService
{
    /// <summary>
    /// Returns active jobs for a specific workflow (paginated when <c>Workflow</c> is set)
    /// or domain-wide active jobs (best-effort union, unpaginated — pagination metadata absent).
    /// </summary>
    Task<Result<MonitorPagedResponse<MonitorJobItem>>> GetActiveJobsAsync(
        MonitorGetActiveJobsInput input,
        CancellationToken cancellationToken = default);
}
