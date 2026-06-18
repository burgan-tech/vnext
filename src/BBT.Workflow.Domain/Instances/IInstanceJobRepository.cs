using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

public interface IInstanceJobRepository : IRepository<InstanceJob, Guid>
{
    Task<List<InstanceJob>> GetListActiveAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active jobs that are candidates for state-scoped cancellation: structured rows
    /// whose <see cref="InstanceJob.JobType"/> is one of <paramref name="matchTypes"/> and whose
    /// <see cref="InstanceJob.TransitionKey"/> is in <paramref name="transitionKeys"/>, plus any
    /// legacy rows (<see cref="JobType.Unknown"/>) for the transitional suffix-based fallback.
    /// </summary>
    Task<List<InstanceJob>> GetActiveForStateCancellationAsync(
        Guid instanceId,
        IReadOnlyCollection<JobType> matchTypes,
        IReadOnlyCollection<string> transitionKeys,
        CancellationToken cancellationToken = default);

    Task MarkAsProcessedAsync(Guid instanceId, string jobName, CancellationToken cancellationToken = default);
    Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> AnyActiveByJobNameAsync(Guid instanceId, string jobName, CancellationToken cancellationToken = default);
}