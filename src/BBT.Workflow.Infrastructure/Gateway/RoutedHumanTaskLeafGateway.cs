using BBT.Aether.Results;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Sends each human-task descent hop to whichever runtime owns it, on the same domain-match
/// predicate <see cref="RoutedInstanceQueryGateway"/> uses.
/// </summary>
public sealed class RoutedHumanTaskLeafGateway(
    IRuntimeInfoProvider runtimeInfoProvider,
    LocalHumanTaskLeafGateway local,
    RemoteHumanTaskLeafGateway remote) : IHumanTaskLeafGateway
{
    /// <inheritdoc />
    public Task<Result<IReadOnlyList<HumanTaskLeafResult>>> ResolveAsync(
        string domain,
        string flow,
        HumanTaskLeafRequest request,
        CancellationToken cancellationToken = default)
    {
        return runtimeInfoProvider.IsDomainMatch(domain)
            ? local.ResolveAsync(domain, flow, request, cancellationToken)
            : remote.ResolveAsync(domain, flow, request, cancellationToken);
    }
}
