using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Sends each correlation hop to whichever runtime owns it, on the same domain-match predicate
/// <see cref="RoutedHumanTaskLeafGateway"/> uses.
/// </summary>
public sealed class RoutedInstanceCorrelationGateway(
    IRuntimeInfoProvider runtimeInfoProvider,
    LocalInstanceCorrelationGateway local,
    RemoteInstanceCorrelationGateway remote) : IInstanceCorrelationGateway
{
    /// <inheritdoc />
    public Task<Result<IReadOnlyList<CorrelationBatchResult>>> ResolveAsync(
        string domain,
        string flow,
        CorrelationBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        return runtimeInfoProvider.IsDomainMatch(domain)
            ? local.ResolveAsync(domain, flow, request, cancellationToken)
            : remote.ResolveAsync(domain, flow, request, cancellationToken);
    }
}
