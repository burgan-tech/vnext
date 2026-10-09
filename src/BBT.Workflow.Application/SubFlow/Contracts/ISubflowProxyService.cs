using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Makes a parent with an active blocking SubFlow a pure proxy for forwardable transitions: no
/// status lock, no Busy flip, no job on the parent — the request goes straight to the active child,
/// which admits it in the mode the parent resolved.
/// </summary>
public interface ISubflowProxyService
{
    /// <summary>
    /// Forwards <paramref name="transitionKey"/> to the snapshot's active SubFlow when the request is
    /// forwardable, and answers with the PARENT's id and the child's status.
    /// </summary>
    /// <param name="snapshot">The parent's intake snapshot (carries the active SubFlow reference).</param>
    /// <param name="workflow">The parent's bound workflow definition.</param>
    /// <param name="transitionKey">The requested transition key.</param>
    /// <param name="input">The caller's transition input.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>null</c> when the request is not forwardable (no active SubFlow; updateData, cancel, exit or
    /// timeout; a parent shared transition available in the current state; an old-version relay that
    /// claims a chain reserve) — the caller then runs it on the parent as before. Otherwise the
    /// forward's outcome.
    /// </returns>
    Task<Result<TransitionOutput>?> TryProxyAsync(
        InstanceExecutionSnapshot snapshot,
        Definitions.Workflow workflow,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken);
}
