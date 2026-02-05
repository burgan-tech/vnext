using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Infrastructure.Gateway;

/// <summary>
/// Routed implementation of instance retry gateway.
/// Routes between local and remote execution based on target domain.
/// Uses IRuntimeInfoProvider.IsDomainMatch() to determine if target domain is local.
/// </summary>
public sealed class RoutedInstanceRetryGateway : IInstanceRetryGateway
{
    private readonly LocalInstanceRetryGateway _local;
    private readonly RemoteInstanceRetryGateway _remote;
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;

    /// <summary>
    /// Initializes a new instance of RoutedInstanceRetryGateway.
    /// </summary>
    /// <param name="local">The local gateway for same-domain execution.</param>
    /// <param name="remote">The remote gateway for cross-domain execution.</param>
    /// <param name="runtimeInfoProvider">Provider for runtime domain information.</param>
    public RoutedInstanceRetryGateway(
        LocalInstanceRetryGateway local,
        RemoteInstanceRetryGateway remote,
        IRuntimeInfoProvider runtimeInfoProvider)
    {
        _local = local;
        _remote = remote;
        _runtimeInfoProvider = runtimeInfoProvider;
    }

    /// <inheritdoc />
    public Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.RetryAsync(input, cancellationToken)
            : _remote.RetryAsync(input, cancellationToken);
    }
}
