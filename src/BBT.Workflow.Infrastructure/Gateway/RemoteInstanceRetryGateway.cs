using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Remote;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Remote implementation of instance retry gateway.
/// Delegates all operations to IRemoteInstanceRetryAppService for HTTP-based execution.
/// Used when target domain differs from the current runtime domain.
/// </summary>
public sealed class RemoteInstanceRetryGateway : IInstanceRetryGateway
{
    private readonly IRemoteInstanceRetryAppService _remoteService;

    /// <summary>
    /// Initializes a new instance of RemoteInstanceRetryGateway.
    /// </summary>
    /// <param name="remoteService">The remote instance retry service for HTTP calls.</param>
    public RemoteInstanceRetryGateway(IRemoteInstanceRetryAppService remoteService)
    {
        _remoteService = remoteService;
    }

    /// <inheritdoc />
    public Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.RetryAsync(input, cancellationToken);
    }
}
