using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Execution.Transitions.Services;

/// <summary>
/// Applies script-context data changes and instance mutations back onto the live
/// <see cref="TransitionExecutionContext"/> exactly once per task phase
/// (OnExecute / OnExit / OnEntry), routing data persistence through the optimistic
/// reconciliation service when enabled and falling back to the legacy fail-fast
/// row replay otherwise.
/// </summary>
public interface IScriptDataChangeApplicator
{
    /// <summary>
    /// Applies the pending script data change set and mutations for one task phase.
    /// </summary>
    /// <param name="transitionContext">The live transition execution context.</param>
    /// <param name="scriptContext">The script context carrying journaled data changes and mutations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Success when changes were applied (or there was nothing to apply); failure when the
    /// reconciliation service exhausted its retries or reported an error — in that case the
    /// journal and mutations are left untouched so the caller can fail the step without persisting.
    /// </returns>
    Task<Result> ApplyAsync(
        TransitionExecutionContext transitionContext,
        ScriptContext scriptContext,
        CancellationToken cancellationToken);
}
