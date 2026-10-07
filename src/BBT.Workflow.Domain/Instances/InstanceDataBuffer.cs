using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// In-memory data line of a <c>history: none</c> instance (vnext#1006). Appends merge into a pending,
/// never-persisted row instead of being written; the write service flushes the accumulated delta in a
/// single INSERT at Finish, at a SubFlow handoff or on an in-pipeline fault.
/// </summary>
/// <remarks>
/// <para>
/// Lives on the aggregate outside the EF <c>DataList</c> navigation, so neither a tracked
/// <c>UpdateAsync</c> nor a detached <c>Attach</c> ever sees the pending row. Snapshots of the
/// aggregate (script context, parallel branches, refreshes) share the SAME buffer, so every branch
/// appends against one in-memory head; the per-instance write gate serializes those appends.
/// </para>
/// <para>
/// A reloaded aggregate has no buffer: paths that run after the stage (post-commit, SubFlow output
/// mapping) append to the database as before.
/// </para>
/// </remarks>
public sealed class InstanceDataBuffer
{
    private readonly Lock _lock = new();
    private readonly List<VersionStrategy> _strategies = [];
    private InstanceData? _baseRow;
    private InstanceData? _pendingRow;
    private JsonData? _accumulatedDelta;

    internal InstanceDataBuffer(InstanceData? baseRow)
    {
        _baseRow = baseRow;
    }

    /// <summary>The persisted latest row the buffer started from (null for a new instance).</summary>
    public InstanceData? BaseRow
    {
        get { lock (_lock) return _baseRow; }
    }

    /// <summary>The merged, not yet persisted latest row; null until the first effective append.</summary>
    public InstanceData? PendingRow
    {
        get { lock (_lock) return _pendingRow; }
    }

    /// <summary>The current in-memory head: the pending row, else the base row.</summary>
    public InstanceData? Head
    {
        get { lock (_lock) return _pendingRow ?? _baseRow; }
    }

    /// <summary>Every delta accepted since the last flush, merged in order.</summary>
    public JsonData? AccumulatedDelta
    {
        get { lock (_lock) return _accumulatedDelta; }
    }

    /// <summary>True when an append changed the data since the last flush.</summary>
    public bool HasPendingChanges
    {
        get { lock (_lock) return _accumulatedDelta is not null; }
    }

    /// <summary>True when <paramref name="id"/> is the pending row's id.</summary>
    public bool IsPendingRow(Guid id)
    {
        lock (_lock) return _pendingRow?.Id == id;
    }

    /// <summary>
    /// Accepts an effective (non-duplicate) append: <paramref name="content"/> becomes the pending
    /// row and <paramref name="accumulatedDelta"/> replaces the accumulated delta.
    /// </summary>
    public InstanceData Accept(
        Guid instanceId,
        JsonData content,
        string dataHash,
        string version,
        JsonData accumulatedDelta,
        VersionStrategy? strategy)
    {
        lock (_lock)
        {
            var row = new InstanceData(Guid.NewGuid(), instanceId, version, content, dataHash, isLatest: true)
            {
                // Informational only: the persisted row's ordinal is assigned at flush.
                VersionNo = (_pendingRow ?? _baseRow)?.VersionNo + 1 ?? 1
            };
            _pendingRow = row;
            _accumulatedDelta = accumulatedDelta;
            _strategies.Add(strategy ?? VersionStrategy.None);
            return row;
        }
    }

    /// <summary>
    /// The version the flushed row carries, folded from the persisted head with every accepted
    /// strategy. A missing head starts at <see cref="WorkflowConstants.DefaultVersion"/> and skips the
    /// first strategy — the same rule as a direct append onto an empty data line.
    /// </summary>
    public string ResolveVersion(string? persistedHeadVersion)
    {
        lock (_lock)
        {
            IEnumerable<VersionStrategy> strategies = _strategies;
            string version;
            if (persistedHeadVersion is null)
            {
                version = WorkflowConstants.DefaultVersion;
                strategies = strategies.Skip(1);
            }
            else
            {
                version = persistedHeadVersion;
            }

            foreach (var strategy in strategies)
                version = InstanceData.IncrementVersion(version, strategy);

            return version;
        }
    }

    /// <summary>
    /// Rebases the buffer after a flush: <paramref name="persisted"/> (null when the flush wrote no row
    /// because nothing changed) becomes the base, and the pending state is cleared.
    /// </summary>
    public void MarkFlushed(InstanceData? persisted)
    {
        lock (_lock)
        {
            if (persisted is not null)
                _baseRow = persisted;
            _pendingRow = null;
            _accumulatedDelta = null;
            _strategies.Clear();
        }
    }
}
