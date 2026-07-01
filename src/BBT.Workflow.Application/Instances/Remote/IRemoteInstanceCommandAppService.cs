using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.SubFlow;

namespace BBT.Workflow.Instances.Remote;

/// <summary>
/// This service acts as a client to the InstanceController endpoints for remote workflow instances.
/// </summary>
public interface IRemoteInstanceCommandAppService
{
    Task<Result<StartInstanceOutput>> StartAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default);
    
    Task<Result<StartInstanceOutput>> StartSubAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default);

    Task<Result<TransitionOutput>> TransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default);

    Task<Result> CompleteAsync(
        FlowCompletedInput input,
        CancellationToken cancellationToken = default);

    Task<Result> UpdateSubFlowStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Propagates SubFlow fault to parent instance by calling the remote API.
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/sub/fault
    /// </summary>
    Task<Result> FaultAsync(
        SubFlowFaultedInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an instance Busy and propagates recursively to nested SubFlows.
    /// PUT {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/busy
    /// </summary>
    Task<Result> MarkBusyAsync(
        MarkBusyInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges a long-poll termination signal on a remote instance, descending its SubFlow chain.
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/longpoll/ack
    /// </summary>
    Task<Result> AcknowledgeLongPollAsync(
        AcknowledgeLongPollInput input,
        CancellationToken cancellationToken = default);
}