using BBT.Aether.Results;
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
} 