using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Service interface for forwarding transitions to active subflow instances.
/// </summary>
public interface ISubflowForwardingService
{
    /// <summary>
    /// Forwards a transition to an active subflow instance.
    /// Returns Result pattern with TransitionOutput on success or Error on failure.
    /// </summary>
    /// <param name="instanceId">The subflow instance ID to forward to.</param>
    /// <param name="transitionKey">The transition key to execute.</param>
    /// <param name="input">The transition input containing domain, workflow, and data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing TransitionOutput on success or Error on failure.</returns>
    Task<Result<TransitionOutput>> ForwardTransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken ct);
}