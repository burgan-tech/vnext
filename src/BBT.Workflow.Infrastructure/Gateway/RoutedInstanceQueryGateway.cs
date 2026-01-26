using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Infrastructure.Gateway;

/// <summary>
/// Routed implementation of instance query gateway.
/// Routes between local and remote execution based on target domain.
/// Uses IRuntimeInfoProvider.IsDomainMatch() to determine if target domain is local.
/// </summary>
public sealed class RoutedInstanceQueryGateway : IInstanceQueryGateway
{
    private readonly LocalInstanceQueryGateway _local;
    private readonly RemoteInstanceQueryGateway _remote;
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;

    /// <summary>
    /// Initializes a new instance of RoutedInstanceQueryGateway.
    /// </summary>
    /// <param name="local">The local gateway for same-domain execution.</param>
    /// <param name="remote">The remote gateway for cross-domain execution.</param>
    /// <param name="runtimeInfoProvider">Provider for runtime domain information.</param>
    public RoutedInstanceQueryGateway(
        LocalInstanceQueryGateway local,
        RemoteInstanceQueryGateway remote,
        IRuntimeInfoProvider runtimeInfoProvider)
    {
        _local = local;
        _remote = remote;
        _runtimeInfoProvider = runtimeInfoProvider;
    }

    /// <inheritdoc />
    public Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.GetInstanceAsync(input, cancellationToken)
            : _remote.GetInstanceAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.GetInstanceDataAsync(input, cancellationToken)
            : _remote.GetInstanceDataAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> GetInstanceListAsync(
        GetInstanceListInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.GetInstanceListAsync(input, cancellationToken)
            : _remote.GetInstanceListAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.GetInstanceHistoryAsync(input, cancellationToken)
            : _remote.GetInstanceHistoryAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetInstanceStateOutput>> GetFunctionWithStateAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.GetFunctionWithStateAsync(input, cancellationToken)
            : _remote.GetFunctionWithStateAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetViewOutput>> GetFunctionWithViewAsync(
        GetFunctionWithInstanceInput input,
        string? platform,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.GetFunctionWithViewAsync(input, platform, transitionKey, cancellationToken)
            : _remote.GetFunctionWithViewAsync(input, platform, transitionKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetSchemaOutput>> GetFunctionWithSchemaAsync(
        GetFunctionWithInstanceInput input,
        string transitionKey,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.GetFunctionWithSchemaAsync(input, transitionKey, cancellationToken)
            : _remote.GetFunctionWithSchemaAsync(input, transitionKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetExtensionsOutput>> GetFunctionWithExtensionsAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.GetFunctionWithExtensionsAsync(input, cancellationToken)
            : _remote.GetFunctionWithExtensionsAsync(input, cancellationToken);
    }
}

