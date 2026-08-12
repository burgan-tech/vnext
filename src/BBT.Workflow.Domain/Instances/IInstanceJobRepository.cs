using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

public interface IInstanceJobRepository : IRepository<InstanceJob, Guid>
{
    Task<List<InstanceJob>> GetListActiveAsync(Guid instanceId, CancellationToken cancellationToken = default);
    Task MarkAsProcessedAsync(Guid instanceId, string jobName, CancellationToken cancellationToken = default);
    Task<InstanceJob?> FindByJobIdAsReadOnlyAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether an active job of the given kind already targets this transition on this instance.
    /// This is the LOGICAL identity of a transition job — matched on the structured columns, never
    /// on <see cref="InstanceJob.JobName"/>, which is unique per enqueue (see <see cref="JobName"/>)
    /// and therefore matches nothing across two enqueues of the same transition.
    /// </summary>
    /// <param name="instanceId">The owning instance.</param>
    /// <param name="jobType">The job kind (async vs scheduled transition).</param>
    /// <param name="sourceState">The state the transition fires from; <c>null</c> matches rows without source-state scoping.</param>
    /// <param name="transitionKey">The targeted transition key.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    Task<bool> AnyActiveTransitionJobAsync(
        Guid instanceId,
        JobType jobType,
        string? sourceState,
        string transitionKey,
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
}