namespace BBT.Workflow.Instances;

/// <summary>
/// The ONLY way an <see cref="InstanceData"/> version is written (architecture decision: no
/// aggregate-side data mutation, no deferred batching). Every append is persisted IMMEDIATELY —
/// task outputs included, parallel or sequential — and the row's whole identity is computed
/// UNDER the per-instance database row lock from the authoritative head:
/// <see cref="InstanceData.VersionNo"/> = head + 1, <see cref="InstanceData.Version"/> =
/// head version + strategy, and the no-change dedup hash from the merged content.
/// </summary>
/// <remarks>
/// Per call, inside the ambient transaction when one is open (otherwise inside a local one):
/// the parent Instances row is locked (<c>FOR UPDATE</c>), the head row (version identity +
/// content hash + content) is read under the lock, the delta is merged onto the head content,
/// identical content is deduplicated (no row), the new row is inserted directly and the given
/// aggregate's in-memory latest snapshot is refreshed. Lock/statement timeouts surface as
/// <c>Instance:100035</c> (409) / <c>Instance:100036</c> (503). The partial unique indexes on
/// <c>InstancesData</c> remain the database-level backstop.
/// </remarks>
public interface IInstanceDataWriteService
{
    /// <summary>
    /// Appends a strategy-versioned data delta: merges it onto the head content read under the
    /// row lock, skips the write entirely when the merged content is byte-identical to the head
    /// (returns <c>null</c>), otherwise computes the version from the head + strategy
    /// (<see cref="VersionStrategy.None"/> keeps the head's version string; a missing strategy
    /// means None) and persists the row immediately. The <paramref name="instance"/> aggregate
    /// (live or snapshot) has its in-memory latest refreshed with the persisted row.
    /// </summary>
    /// <param name="instance">The aggregate whose data line is appended; also refreshed in memory.</param>
    /// <param name="delta">The data delta produced by the caller (payload mapping, task output…).</param>
    /// <param name="versionStrategy">Semantic version strategy; null behaves as None.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The persisted row, or <c>null</c> when the merge produced no content change.</returns>
    Task<InstanceData?> AppendAsync(
        Instance instance,
        JsonData delta,
        VersionStrategy? versionStrategy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a row with an EXPLICIT, caller-authored version (the definition publish path):
    /// no merge — the payload is stored as authored. Under the same row lock, an existing row
    /// with the same version short-circuits (returns it, no write); otherwise the head
    /// comparison decides whether the new row takes the latest flag (an older-line version
    /// never steals it). The aggregate's in-memory state is refreshed with the persisted row.
    /// </summary>
    /// <param name="instance">The aggregate whose data line is appended; also refreshed in memory.</param>
    /// <param name="id">The id for the new row.</param>
    /// <param name="version">The explicit semantic version to store.</param>
    /// <param name="data">The payload, stored as authored (no merge).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The persisted row, or the pre-existing row when the version already exists.</returns>
    Task<InstanceData> AppendExplicitAsync(
        Instance instance,
        Guid id,
        string version,
        JsonData data,
        CancellationToken cancellationToken = default);
}
