using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

public interface IInstanceJobRepository : IRepository<InstanceJob, Guid>
{
    Task<List<InstanceJob>> GetListActiveAsync(Guid instanceId, CancellationToken cancellationToken = default);
    Task MarkAsProcessedAsync(Guid instanceId, string jobName, CancellationToken cancellationToken = default);
    Task<bool> MarkAsProcessedByJobIdAsync(
        Guid jobId,
        Guid processingToken,
        CancellationToken cancellationToken = default);
    Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<InstanceJob?> FindByIdempotencyKeyAsReadOnlyAsync(
        Guid instanceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
    Task<bool> AnyActiveByJobNameAsync(Guid instanceId, string jobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims an active job for one delivery. A crashed claim becomes eligible again
    /// after <paramref name="leaseDuration"/>; concurrent/redelivered jobs receive <c>false</c>.
    /// </summary>
    Task<bool> TryClaimAsync(
        Guid jobId,
        Guid processingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether the active, unexpired processing lease is still owned by
    /// <paramref name="processingToken"/>. Used to fence recovery side effects.
    /// </summary>
    Task<bool> IsClaimOwnerAsync(
        Guid jobId,
        Guid processingToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a processing claim without making the job terminal. Used during host shutdown so
    /// the dispatcher retry can reclaim immediately instead of waiting for the old lease to expire.
    /// </summary>
    Task<bool> ReleaseClaimAsync(
        Guid jobId,
        Guid processingToken,
        CancellationToken cancellationToken = default);

    Task<bool> MarkAsFailedAsync(
        Guid jobId,
        Guid processingToken,
        string errorCode,
        string? errorDetails = null,
        CancellationToken cancellationToken = default);

    Task<bool> MarkAsSupersededAsync(
        Guid jobId,
        Guid processingToken,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: active jobs for a given flow (workflow) in the current schema, optionally
    /// bounded by a createdAt range. Additive monitor-support method.
    /// </summary>
    /// <param name="flow">The workflow flow name.</param>
    /// <param name="createdAtGte">Optional inclusive lower bound on <see cref="InstanceJob.CreatedAt"/>.</param>
    /// <param name="createdAtLte">Optional inclusive upper bound on <see cref="InstanceJob.CreatedAt"/>.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A list of active <see cref="InstanceJob"/> records for the specified flow.</returns>
    Task<List<InstanceJob>> GetActiveByFlowAsync(
        string flow,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: a page of active jobs for a given flow in the current schema, optionally
    /// bounded by a createdAt range. Fetches one extra row (<c>take + 1</c>) so the caller can
    /// compute HasNext without a COUNT query. Additive monitor-support method.
    /// </summary>
    /// <param name="flow">The workflow flow name.</param>
    /// <param name="createdAtGte">Optional inclusive lower bound on <see cref="InstanceJob.CreatedAt"/>.</param>
    /// <param name="createdAtLte">Optional inclusive upper bound on <see cref="InstanceJob.CreatedAt"/>.</param>
    /// <param name="skip">Number of rows to skip.</param>
    /// <param name="take">Page size; the query fetches <c>take + 1</c> rows for next-page detection.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>Up to <c>take + 1</c> active <see cref="InstanceJob"/> records, newest first.</returns>
    Task<List<InstanceJob>> GetActiveByFlowPagedAsync(
        string flow,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: all active jobs for a given domain in the resolved schema (best-effort).
    /// Additive monitor-support method.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A list of active <see cref="InstanceJob"/> records for the specified domain.</returns>
    Task<List<InstanceJob>> GetActiveByDomainAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the subset of <paramref name="instanceIds"/> that have at least one live job.
    /// Only chain-driving async-transition jobs qualify. Scheduled/pending-dispatch jobs are live
    /// only when last touched on or after <paramref name="pendingDispatchCutoff"/>; processing jobs
    /// are live only while their lease extends past <paramref name="utcNow"/>. Used by the chain
    /// reaper to avoid N+1 queries.
    /// </summary>
    Task<HashSet<Guid>> GetInstanceIdsWithActiveJobAsync(
        IEnumerable<Guid> instanceIds,
        DateTime pendingDispatchCutoff,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
