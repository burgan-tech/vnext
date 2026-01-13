using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.SubFlow;

namespace BBT.Workflow.Infrastructure.Gateway;

/// <summary>
/// Remote implementation of instance command gateway.
/// Delegates all operations to IRemoteInstanceCommandAppService for HTTP-based execution.
/// Used when target domain differs from the current runtime domain.
/// </summary>
public sealed class RemoteInstanceCommandGateway : IInstanceCommandGateway
{
    private readonly IRemoteInstanceCommandAppService _remoteService;

    /// <summary>
    /// Initializes a new instance of RemoteInstanceCommandGateway.
    /// </summary>
    /// <param name="remoteService">The remote instance command service for HTTP calls.</param>
    public RemoteInstanceCommandGateway(IRemoteInstanceCommandAppService remoteService)
    {
        _remoteService = remoteService;
    }

    /// <inheritdoc />
    public Task<Result<StartInstanceOutput>> StartAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.StartAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<StartInstanceOutput>> StartSubAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.StartSubAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TransitionOutput>> TransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.TransitionAsync(instanceId, transitionKey, input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> CompleteAsync(
        FlowCompletedInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.CompleteAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> UpdateSubFlowStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.UpdateSubFlowStateAsync(input, cancellationToken);
    }
}

