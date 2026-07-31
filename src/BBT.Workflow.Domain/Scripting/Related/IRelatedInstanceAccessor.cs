using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Script-facing access to instances related to the current one — one hop up (the parent that started
/// this instance as a SubFlow/SubProcess) or one hop down (this instance's own correlations).
/// Exposed as <c>ScriptContext.Related</c>.
/// </summary>
/// <remarks>
/// Nothing is pre-fetched: the first call that needs data performs the read, and results are memoized
/// for the lifetime of the owning ScriptContext. Reads are unfiltered — copying a related instance's
/// field into the current instance's data makes it reachable by any client entitled to read the
/// current instance, because x-roles protection does not follow the copy.
/// </remarks>
public interface IRelatedInstanceAccessor
{
    /// <summary>
    /// True when this instance was started by a parent as a SubFlow or SubProcess.
    /// Reads instance metadata only — never performs a data read.
    /// </summary>
    bool HasParent { get; }

    /// <summary>
    /// Sub workflow keys of this instance's correlations, in correlation creation order, duplicates
    /// removed. Loads the correlation list (once) but reads no instance data.
    /// </summary>
    Task<IReadOnlyList<string>> SubKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The parent instance, or null when this instance has no parent.
    /// </summary>
    /// <exception cref="RelatedInstanceAccessException">The read failed, or the cap was exceeded.</exception>
    Task<RelatedInstanceView?> ParentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recently created correlation whose sub workflow key matches, or null when there is none.
    /// </summary>
    /// <param name="subFlowKey">Sub workflow key, matched against <c>InstanceCorrelation.SubFlowName</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RelatedInstanceAccessException">The read failed, or the cap was exceeded.</exception>
    Task<RelatedInstanceView?> SubAsync(string subFlowKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// All correlations ordered by creation time, optionally filtered by sub workflow key.
    /// Active and completed correlations are both included. Reads are batched — never N+1.
    /// </summary>
    /// <param name="subFlowKey">Sub workflow key filter, or null for every correlation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RelatedInstanceAccessException">The read failed, or the cap was exceeded.</exception>
    Task<IReadOnlyList<RelatedInstanceView>> SubsAsync(
        string? subFlowKey = null,
        CancellationToken cancellationToken = default);
}
