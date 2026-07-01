using BBT.Aether.Results;
using BBT.Workflow.Monitor.Common.DTOs;
using BBT.Workflow.Monitor.Instances.DTOs;

namespace BBT.Workflow.Monitor.Instances;

/// <summary>
/// Read-only, cross-schema query service that lists faulted instances across every workflow
/// schema in a domain within a mandatory createdAt time window.
/// </summary>
public interface IMonitorDomainFaultService
{
    /// <summary>
    /// Scans every workflow schema in the domain for faulted instances matching the supplied
    /// bounded createdAt filter and returns the unioned list ordered by createdAt descending.
    /// </summary>
    /// <param name="input">Domain and the mandatory bounded GraphQL filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The unioned faulted instances, or a validation failure.</returns>
    Task<Result<MonitorPagedResponse<MonitorInstanceResponse>>> GetDomainFaultedInstancesAsync(
        MonitorGetDomainFaultedInput input,
        CancellationToken cancellationToken = default);
}
