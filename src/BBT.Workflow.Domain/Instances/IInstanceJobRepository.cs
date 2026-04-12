using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

public interface IInstanceJobRepository : IRepository<InstanceJob, Guid>
{
    Task<List<InstanceJob>> GetListActiveAsync(Guid instanceId, CancellationToken cancellationToken = default);
    Task MarkAsProcessedAsync(Guid instanceId, string jobName, CancellationToken cancellationToken = default);
    Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> AnyActiveByJobNameAsync(Guid instanceId, string jobName, CancellationToken cancellationToken = default);
}