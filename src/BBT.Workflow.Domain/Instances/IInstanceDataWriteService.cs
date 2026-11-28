using BBT.Aether.Results;

namespace BBT.Workflow.Instances;

/// <summary>
/// The ONLY way an <see cref="InstanceData"/> version is written (architecture decision: no
/// aggregate-side data mutation). The one deferred case is a <c>history: none</c> instance
/// (<see cref="Instance.IsDataBuffered"/>, vnext#1006): its appends merge into the in-memory
/// <see cref="InstanceDataBuffer"/> and <see cref="FlushAsync"/> writes them once. Otherwise every append is persisted IMMEDIATELY —
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
    /// <param name="workflow">
    /// The instance's workflow definition, supplied by the caller so the merged content can be
    /// validated against the flow's master schema. Callers that have no definition in hand (the
    /// publish path, system writes) pass <c>null</c> and the validation is skipped — the same
    /// outcome those paths already had when the definition was read from an ambient scope.
    /// </param>
    /// <param name="touchInstance">
    /// <c>true</c> when this append IS the instance's modification: a data row written by
    /// <c>updateData</c> (its request body or one of its tasks' output), the one transition whose outcome
    /// writes nothing to the instance row. The row's <c>EnteredAt</c> is then stamped as
    /// <c>Instances.ModifiedAt</c> in the same batch and transaction as the insert, and a tracked
    /// aggregate is left in sync. <c>false</c> (default) for an append made inside any other
    /// transition: its own outcome stamps the instance once, so a per-append stamp would only add row
    /// versions to the hottest table. A duplicate payload writes no row and stamps nothing.
    /// </param>
    /// <returns>The persisted row, or <c>null</c> when the merge produced no content change.</returns>
    Task<InstanceData?> AppendAsync(
        Instance instance,
        JsonData delta,
        VersionStrategy? versionStrategy,
        CancellationToken cancellationToken = default,
        Definitions.Workflow? workflow = null,
        bool touchInstance = false);

    /// <summary>
    /// <see cref="AppendAsync"/> plus a write that must commit with it: <paramref name="inSameTransaction"/>
    /// runs inside the row-lock transaction after the row is written (or deduplicated), so the data
    /// row and the caller's write commit together — one commit instead of two. The task engine uses it
    /// for the task's journal row. The callback must issue its statements on the shared context's
    /// connection without <c>SaveChanges</c> (raw or set-based SQL), must not call back into this
    /// service (the per-instance gate is not re-entrant) and must do no slow I/O: the instance row is
    /// held <c>FOR UPDATE</c> while it runs. For a buffered (<c>history: none</c>) instance nothing is
    /// written yet, and the callback runs on its own after the buffered append.
    /// </summary>
    /// <returns>The persisted row, or <c>null</c> when the merge produced no content change.</returns>
    Task<InstanceData?> AppendWithAsync(
        Instance instance,
        JsonData delta,
        VersionStrategy? versionStrategy,
        Func<CancellationToken, Task> inSameTransaction,
        CancellationToken cancellationToken = default,
        Definitions.Workflow? workflow = null,
        bool touchInstance = false);

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
    /// <param name="workflow">
    /// The instance's workflow definition for master-schema validation; <c>null</c> skips it.
    /// See <see cref="AppendAsync"/>.
    /// </param>
    /// <returns>The persisted row, or the pre-existing row when the version already exists.</returns>
    Task<InstanceData> AppendExplicitAsync(
        Instance instance,
        Guid id,
        string version,
        JsonData data,
        CancellationToken cancellationToken = default,
        Definitions.Workflow? workflow = null);

    /// <summary>
    /// Writes the buffered data of a <c>history: none</c> instance (vnext#1006) as ONE row: the
    /// accumulated delta is merged onto the persisted head under the row lock (encryption, hashing and
    /// validation as for <see cref="AppendAsync"/>), and the buffer is rebased onto the written row.
    /// <paramref name="inSameTransaction"/> runs inside the same transaction after the write — the
    /// caller's own save (completion, correlation) commits atomically with the data. It must not call
    /// <see cref="AppendAsync"/>: the per-instance gate is not re-entrant. Without a buffer or pending
    /// change only the callback runs.
    /// </summary>
    /// <returns>The persisted row, or <c>null</c> when nothing was written.</returns>
    Task<InstanceData?> FlushAsync(
        Instance instance,
        Definitions.Workflow? workflow,
        Func<CancellationToken, Task>? inSameTransaction = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Side-effect-free preview of <see cref="AppendAsync"/> (vnext#1255): merges
    /// <paramref name="delta"/> onto the aggregate's in-memory latest data with the append's own
    /// rules and validates the result against the flow's master schema. Nothing is locked, written
    /// or changed on <paramref name="instance"/>. Used at transition intake so a request body the
    /// append would refuse is answered as a validation failure before admission.
    /// </summary>
    /// <remarks>
    /// An early refusal only, never the authority: the head is the caller's loaded snapshot, not the
    /// one read under the row lock, and <see cref="AppendAsync"/> validates again. Whenever the preview
    /// cannot reproduce what the append would validate it accepts and leaves the verdict to the append:
    /// no master schema or one that failed to load, a schema with <c>x-encryption</c> paths or data
    /// already carrying protected values, a delta carrying a file node (<c>x-storage</c> is swapped
    /// after validation), a non-object delta, or a merge that changes nothing (the append writes and
    /// validates nothing then). An unexpected failure of the preview itself is accepted the same way
    /// and logged (<c>IntakeMasterSchemaPreviewSkipped</c>); only cancellation propagates.
    /// </remarks>
    /// <param name="instance">The aggregate whose latest data is the merge base; not modified.</param>
    /// <param name="delta">The data the append would receive.</param>
    /// <param name="workflow">The instance's workflow definition, naming the master schema.</param>
    /// <param name="culture">Culture for the validation messages; <c>null</c> uses the default.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>Success, or the master-schema validation failure with its validation errors.</returns>
    Task<Result> PreviewAppendAsync(
        Instance instance,
        JsonData delta,
        Definitions.Workflow workflow,
        string? culture = null,
        CancellationToken cancellationToken = default);
}
