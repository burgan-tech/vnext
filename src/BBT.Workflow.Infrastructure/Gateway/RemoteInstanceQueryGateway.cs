using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Instances.Remote;

namespace BBT.Workflow.Infrastructure.Gateway;

/// <summary>
/// Remote implementation of instance query gateway.
/// Delegates all operations to IRemoteInstanceQueryAppService for HTTP-based execution.
/// Used when target domain differs from the current runtime domain.
/// </summary>
public sealed class RemoteInstanceQueryGateway : IInstanceQueryGateway
{
    private readonly IRemoteInstanceQueryAppService _remoteService;

    /// <summary>
    /// Initializes a new instance of RemoteInstanceQueryGateway.
    /// </summary>
    /// <param name="remoteService">The remote instance query service for HTTP calls.</param>
    public RemoteInstanceQueryGateway(IRemoteInstanceQueryAppService remoteService)
    {
        _remoteService = remoteService;
    }

    /// <inheritdoc />
    public Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.GetInstanceAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.GetInstanceDataAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.GetInstanceHistoryAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetInstanceStateOutput>> GetFunctionWithStateAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.GetFunctionWithStateAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetViewOutput>> GetFunctionWithViewAsync(
        GetFunctionWithInstanceInput input,
        string? platform,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.GetFunctionWithViewAsync(input, platform, transitionKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetSchemaOutput>> GetFunctionWithSchemaAsync(
        GetFunctionWithInstanceInput input,
        string transitionKey,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.GetFunctionWithSchemaAsync(input, transitionKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetExtensionsOutput>> GetFunctionWithExtensionsAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.GetFunctionWithExtensionsAsync(input, cancellationToken);
    }
}

