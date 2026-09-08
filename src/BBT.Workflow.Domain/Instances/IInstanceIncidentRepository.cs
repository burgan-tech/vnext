using BBT.Aether.Domain.Repositories;
using BBT.Aether;

namespace BBT.Workflow.Instances;

/// <summary>
/// Repository for <see cref="InstanceIncident"/> rows read independently of the
/// <see cref="Instance"/> aggregate: the active incident and the paged history.
/// Writes still go through the aggregate (<see cref="Instance.AddIncident"/> +
/// <c>IInstanceRepository.UpdateAsync</c>) so the denormalized <see cref="Instance.HasActiveIncident"/>
/// flag and the incident rows change in the same unit of work.
/// </summary>
public interface IInstanceIncidentRepository : IRepository<InstanceIncident, Guid>
{
    /// <summary>
    /// Pages the full incident history of an instance, newest first (<c>CreatedAt DESC, Id DESC</c>).
    /// No-tracking read.
    /// </summary>
    /// <param name="instanceId">Owning instance identifier.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<HateoasPagedList<InstanceIncident>> GetHistoryPagedAsync(
        Guid instanceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the newest unresolved incident of an instance, or null when none is open. Backs the
    /// active-incident endpoint the <c>incident.active</c> link points at. No-tracking read.
    /// </summary>
    Task<InstanceIncident?> GetActiveAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every open incident of an instance resolved, directly in the database.
    /// <para>
    /// Needed when the aggregate holding the incidents was loaded outside the change tracker — the
    /// retry path reads the instance no-tracking and unfaults it inside a <c>RequiresNew</c> scope,
    /// so the in-memory <c>Resolve()</c> has no tracked row behind it. A set-based update also keeps
    /// those rows out of the aggregate's navigation, which is what prevents a second context from
    /// re-inserting them.
    /// </para>
    /// <para>
    /// Idempotent: the predicate already excludes resolved rows, so a repeat call affects nothing.
    /// Incidents recorded by the pipeline in its own unit of work are tracked and are saved by the
    /// graph instead — see <c>FinalizeTransitionStep</c>.
    /// </para>
    /// </summary>
    /// <returns>The number of incidents this call closed; 0 when none was open.</returns>
    Task<int> ResolveAllAsync(
        Guid instanceId,
        DateTime resolvedAt,
        CancellationToken cancellationToken = default);
}
