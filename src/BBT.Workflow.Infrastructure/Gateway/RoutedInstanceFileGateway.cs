using BBT.Aether.Results;
using BBT.Workflow.Files;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Routes service-to-service file reads: local when the domain is this runtime's, remote otherwise. Both halves
/// are injected as interfaces (keyed DI) so the routing decision itself is unit-testable, like
/// <see cref="RoutedRelatedInstanceReader"/>.
/// </summary>
public sealed class RoutedInstanceFileGateway(
    IRuntimeInfoProvider runtimeInfoProvider,
    [FromKeyedServices(InstanceFileGatewayKeys.Local)] IInstanceFileGateway local,
    [FromKeyedServices(InstanceFileGatewayKeys.Remote)] IInstanceFileGateway remote) : IInstanceFileGateway
{
    /// <inheritdoc />
    public Task<Result<InstanceFileContent>> ReadAsync(
        string domain, string flow, string instance, string file, CancellationToken cancellationToken) =>
        runtimeInfoProvider.IsDomainMatch(domain)
            ? LocalDomainCall.Run(domain, () => local.ReadAsync(domain, flow, instance, file, cancellationToken))
            : remote.ReadAsync(domain, flow, instance, file, cancellationToken);
}
