using BBT.Aether.Results;

namespace BBT.Workflow.Instances.HumanTask;

/// <summary>
/// Walks a human-task candidate down its active SubFlow chain to the instance that is actually
/// waiting, and answers the two questions the list needs about that leaf: may this caller act on
/// it, and what does it say.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the list descended exactly one level, resolved the child's definition
/// against the CALLER's domain instead of the correlation's, and read the title from the ROOT. So a
/// task two levels down was authorized against the wrong state, and a cross-domain child resolved
/// to nothing and silently removed its whole instance from the list.
/// </para>
/// <para>
/// The walk is batched by level, not by instance: every candidate's next hop is grouped by
/// (domain, flow) and resolved in one call per group. A cross-domain group hands the REST of the
/// descent to the domain that owns it, which recurses locally and answers with a finished result —
/// so the cost is one remote call per domain boundary crossed, not one per instance per level.
/// </para>
/// </remarks>
public interface IHumanTaskLeafResolver
{
    /// <summary>
    /// Resolves one level's worth of instances, descending as far as needed.
    /// </summary>
    /// <param name="domain">Domain owning the instances in <paramref name="request"/>.</param>
    /// <param name="flow">Workflow key naming their schema.</param>
    /// <param name="request">The ids, the caller's roles and headers, and the remaining depth.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<IReadOnlyList<HumanTaskLeafResult>>> ResolveAsync(
        string domain,
        string flow,
        HumanTaskLeafRequest request,
        CancellationToken cancellationToken = default);
}
