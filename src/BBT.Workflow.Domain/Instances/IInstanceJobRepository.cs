using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

public interface IInstanceJobRepository : IRepository<InstanceJob, Guid>
{
    Task<List<InstanceJob>> GetListActiveAsync(Guid instanceId, CancellationToken cancellationToken = default);
    Task MarkAsProcessedAsync(Guid instanceId, string jobName, CancellationToken cancellationToken = default);
    Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> AnyActiveByJobNameAsync(Guid instanceId, string jobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the subset of <paramref name="instanceIds"/> that have at least one active job.
    /// Used by the chain reaper to avoid N+1 queries when checking a batch of stuck instances.
    /// </summary>
    Task<HashSet<Guid>> GetInstanceIdsWithActiveJobAsync(IEnumerable<Guid> instanceIds, CancellationToken cancellationToken = default);
}