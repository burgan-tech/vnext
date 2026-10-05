using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Routes one hop of a correlation-tree walk to whichever runtime owns it: in-process when the
/// target is this domain, over the internal batch endpoint when it is not.
/// </summary>
/// <remarks>
/// The same shape as <c>IHumanTaskLeafGateway</c> — a routed implementation picks local or remote
/// from <c>IRuntimeInfoProvider.IsDomainMatch</c>, and the far side re-enters the identical
/// resolver. That is what makes a cross-domain branch cost ONE call for the whole branch instead
/// of one per level, and it is why the previous implementation's inability to leave the local
/// database is fixed by routing rather than by a second code path.
/// </remarks>
public interface IInstanceCorrelationGateway
{
    /// <summary>Expands a batch of instances in <paramref name="domain"/>'s <paramref name="flow"/>.</summary>
    Task<Result<IReadOnlyList<CorrelationBatchResult>>> ResolveAsync(
        string domain,
        string flow,
        CorrelationBatchRequest request,
        CancellationToken cancellationToken = default);
}
