using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

public interface IInstanceJobRepository : IRepository<InstanceJob, Guid>
{
    Task<List<InstanceJob>> GetListActiveAsync(Guid instanceId, CancellationToken cancellationToken = default);
    Task MarkAsProcessedAsync(string jobName, CancellationToken cancellationToken = default);
    Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: all active jobs for a given flow (workflow) in the current schema.
    /// Additive monitor-support method.
    /// </summary>
    /// <param name="flow">The workflow flow name.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A list of active <see cref="InstanceJob"/> records for the specified flow.</returns>
    Task<List<InstanceJob>> GetActiveByFlowAsync(string flow, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: all active jobs for a given domain in the resolved schema (best-effort).
    /// Additive monitor-support method.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A list of active <see cref="InstanceJob"/> records for the specified domain.</returns>
    Task<List<InstanceJob>> GetActiveByDomainAsync(string domain, CancellationToken cancellationToken = default);
}