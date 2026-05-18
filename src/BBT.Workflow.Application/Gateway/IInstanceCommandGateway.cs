using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.SubFlow;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Gateway interface for instance command operations.
/// Routes between local and remote execution based on target domain.
/// When target domain matches the current runtime, executes locally with proper transaction.
/// When target domain differs, delegates to remote HTTP service.
/// </summary>
public interface IInstanceCommandGateway
{
    /// <summary>
    /// Starts a new workflow instance.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The start instance input containing domain, workflow, and instance data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the started instance output.</returns>
    Task<Result<StartInstanceOutput>> StartAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a new sub workflow instance (SubFlow/SubProcess).
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The start instance input containing domain, workflow, and instance data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the started instance output.</returns>
    Task<Result<StartInstanceOutput>> StartSubAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a transition on an existing workflow instance.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="instanceId">The instance identifier (GUID).</param>
    /// <param name="transitionKey">The transition key to execute.</param>
    /// <param name="input">The transition input containing domain, workflow, and transition data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the transition output.</returns>
    Task<Result<TransitionOutput>> TransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a sub workflow instance and notifies the parent.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The flow completed input containing domain, flow, and completion data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success or failure.</returns>
    Task<Result> CompleteAsync(
        FlowCompletedInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the parent instance with SubFlow's state change.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The SubFlow state change input containing parent and state information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success or failure.</returns>
    Task<Result> UpdateSubFlowStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles upward SubFlow fault propagation to the parent instance.
    /// Routes to local or remote based on target domain in input.
    /// </summary>
    /// <param name="input">The SubFlow fault data including incident details for the parent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success or failure.</returns>
    Task<Result> FaultAsync(
        SubFlowFaultedInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a workflow instance as Busy and recursively propagates to active SubFlow correlations.
    /// Routes to local or remote execution based on target domain.
    /// Each level marks itself Busy, then checks for an active SubFlow correlation and
    /// calls <see cref="MarkBusyAsync"/> for the nested instance (cross-domain routing).
    /// </summary>
    /// <param name="input">Target domain, workflow, and instance id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success or failure of remote or local persistence.</returns>
    Task<Result> MarkBusyAsync(
        MarkBusyInput input,
        CancellationToken cancellationToken = default);
}

