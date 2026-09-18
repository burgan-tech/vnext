using BBT.Aether.Results;
using BBT.Workflow.Instances.HumanTask;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Routes one level of a human-task descent to whichever runtime owns it: in-process when the
/// target is this domain, over the internal endpoint when it is not.
/// </summary>
/// <remarks>
/// The same shape as <c>IInstanceQueryGateway</c> — a routed implementation picks local or remote
/// from <c>IRuntimeInfoProvider.IsDomainMatch</c>, and the far side re-enters the identical
/// resolver, so a chain that crosses domains twice costs two calls rather than two per instance.
/// </remarks>
public interface IHumanTaskLeafGateway
{
    /// <summary>Resolves a batch of instances in <paramref name="domain"/>'s <paramref name="flow"/>.</summary>
    Task<Result<IReadOnlyList<HumanTaskLeafResult>>> ResolveAsync(
        string domain,
        string flow,
        HumanTaskLeafRequest request,
        CancellationToken cancellationToken = default);
}
