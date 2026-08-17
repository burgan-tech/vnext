using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Routed implementation of instance command gateway.
/// Routes between local and remote execution based on target domain.
/// Uses IRuntimeInfoProvider.IsDomainMatch() to determine if target domain is local.
/// </summary>
public sealed class RoutedInstanceCommandGateway : IInstanceCommandGateway
{
    private readonly LocalInstanceCommandGateway _local;
    private readonly RemoteInstanceCommandGateway _remote;
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;

    /// <summary>
    /// Initializes a new instance of RoutedInstanceCommandGateway.
    /// </summary>
    /// <param name="local">The local gateway for same-domain execution.</param>
    /// <param name="remote">The remote gateway for cross-domain execution.</param>
    /// <param name="runtimeInfoProvider">Provider for runtime domain information.</param>
    public RoutedInstanceCommandGateway(
        LocalInstanceCommandGateway local,
        RemoteInstanceCommandGateway remote,
        IRuntimeInfoProvider runtimeInfoProvider)
    {
        _local = local;
        _remote = remote;
        _runtimeInfoProvider = runtimeInfoProvider;
    }

    /// <inheritdoc />
    public Task<Result<StartInstanceOutput>> StartAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.StartAsync(input, cancellationToken)
            : _remote.StartAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<StartInstanceOutput>> StartSubAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.StartSubAsync(input, cancellationToken)
            : _remote.StartSubAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TransitionOutput>> TransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.TransitionAsync(instanceId, transitionKey, input, cancellationToken)
            : _remote.TransitionAsync(instanceId, transitionKey, input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> CancelChildAsync(
        Guid instanceId,
        string domain,
        string flow,
        ChildSubflowCancelInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(domain)
            ? _local.CancelChildAsync(instanceId, domain, flow, input, cancellationToken)
            : _remote.CancelChildAsync(instanceId, domain, flow, input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> CompleteAsync(
        FlowCompletedInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.CompleteAsync(input, cancellationToken)
            : _remote.CompleteAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> UpdateSubFlowStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.UpdateSubFlowStateAsync(input, cancellationToken)
            : _remote.UpdateSubFlowStateAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> FaultAsync(
        SubFlowFaultedInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.FaultAsync(input, cancellationToken)
            : _remote.FaultAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> CancelAsync(
        SubItemCanceledInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.CancelAsync(input, cancellationToken)
            : _remote.CancelAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> MarkBusyAsync(
        MarkBusyInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.MarkBusyAsync(input, cancellationToken)
            : _remote.MarkBusyAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TransitionOutput>> ForwardTransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.ForwardTransitionAsync(instanceId, transitionKey, input, cancellationToken)
            : _remote.ForwardTransitionAsync(instanceId, transitionKey, input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> ReleaseBusyAsync(
        MarkBusyInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.ReleaseBusyAsync(input, cancellationToken)
            : _remote.ReleaseBusyAsync(input, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> AcknowledgeLongPollAsync(
        AcknowledgeLongPollInput input,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(input.Domain)
            ? _local.AcknowledgeLongPollAsync(input, cancellationToken)
            : _remote.AcknowledgeLongPollAsync(input, cancellationToken);
    }
}
