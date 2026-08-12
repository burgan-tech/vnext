namespace BBT.Workflow.Instances;

/// <summary>
/// The single owner of persisting new <see cref="InstanceData"/> versions. Every code path that
/// adds a data version to an <see cref="Instance"/> aggregate (start, transition mapped data,
/// task results, subflow output mapping, definition seeds) must persist through this service
/// instead of a plain repository save — it serializes concurrent writers with a per-instance
/// database row lock and assigns the version identity under that lock.
/// </summary>
/// <remarks>
/// Per call, inside the ambient transaction when one is open (otherwise inside a local one):
/// the parent Instances row is locked (<c>FOR UPDATE</c>), the authoritative InstanceData head is
/// read under the lock, monotonic <see cref="InstanceData.VersionNo"/>s are assigned (head + 1),
/// stale semantic versions are rebased onto the real head, any stale latest row is demoted, and
/// the pending changes are saved. Lock/statement timeouts surface as
/// <c>Instance:100035</c> (409) / <c>Instance:100036</c> (503). The partial unique indexes on
/// <c>InstancesData</c> remain the database-level backstop.
/// </remarks>
public interface IInstanceDataWriteService
{
    /// <summary>
    /// Persists the pending changes of <paramref name="instance"/>, versioning its newly added
    /// <see cref="InstanceData"/> rows under the per-instance row lock. Also flushes every other
    /// pending change on the unit of work's context (it performs the SaveChanges the call site
    /// would otherwise have requested with <c>autoSave: true</c>). Safe to call when no new data
    /// rows are pending — it degrades to a plain save.
    /// </summary>
    /// <param name="instance">The aggregate whose newly added data versions are being persisted.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    Task SaveWithVersioningAsync(Instance instance, CancellationToken cancellationToken = default);
}
