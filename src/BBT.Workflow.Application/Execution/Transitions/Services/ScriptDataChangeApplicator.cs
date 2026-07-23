using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Transitions.Services;

/// <summary>
/// Default <see cref="IScriptDataChangeApplicator"/> implementation.
/// <para>
/// When <see cref="WorkflowExecutionOptions.EnableInstanceDataReconciliation"/> is off, it
/// delegates to the legacy <see cref="TransitionExecutionContext.ApplyScriptContextChanges"/>
/// fail-fast row replay. When on, it drains the script context's journaled data change set
/// through <see cref="IInstanceDataReconciliationService"/> (bounded rebase retries live inside
/// that service), propagates the reconciled data payload onto the context, and refreshes the
/// script context snapshot before applying instance mutations. Tracked-aggregate row
/// synchronization is owned by the repository append itself — see the invariant note in
/// <see cref="ApplyAsync"/>. On reconciliation failure the journal and mutations are
/// intentionally left untouched.
/// </para>
/// </summary>
public sealed class ScriptDataChangeApplicator(
    IInstanceDataReconciliationService reconciliationService,
    IOptions<WorkflowExecutionOptions> options) : IScriptDataChangeApplicator
{
    /// <inheritdoc />
    public async Task<Result> ApplyAsync(
        TransitionExecutionContext transitionContext,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transitionContext);
        ArgumentNullException.ThrowIfNull(scriptContext);

        if (!options.Value.EnableInstanceDataReconciliation)
        {
            transitionContext.ApplyScriptContextChanges(scriptContext);
            return Result.Ok();
        }

        // Mirror the legacy no-op guard: without both a script snapshot and a live aggregate
        // there is nothing to reconcile, and legacy also skips mutations in that case.
        if (scriptContext.Instance is null || transitionContext.Instance is null)
        {
            return Result.Ok();
        }

        var changeSet = scriptContext.Instance.GetPendingDataChangeSet();
        if (changeSet is not null)
        {
            // Observability: the reconciliation service reads this tag from Activity.Current
            // (it has no access to the transition context by design). Neither this applicator
            // nor the service starts a child Activity, so the read happens on the same
            // Activity instance that receives the tag here.
            Activity.Current?.SetTag("workflow.transition.key", transitionContext.TransitionKey);

            var reconciled = await reconciliationService.ApplyAsync(
                transitionContext.Instance, changeSet, cancellationToken);
            if (!reconciled.IsSuccess)
            {
                return Result.Fail(reconciled.Error);
            }

            var value = reconciled.Value!;

            // INVARIANT: the live aggregate is deliberately NOT synchronized here.
            // - When AppendedData is non-empty, the repository append
            //   (EfCoreInstanceRepository.TryAppendDataAsync) has already attached the returned
            //   rows as Unchanged and synchronized the tracked Instance aggregate itself, so a
            //   second synchronization would be redundant.
            // - When AppendedData is empty, the service short-circuited (batch already applied
            //   by a competing writer, or replay no-op) and LatestData is a DETACHED row
            //   rehydrated from a head read; pushing it into the tracked aggregate would let
            //   EF change tracking mark it Added and re-insert an already persisted row
            //   (duplicate primary key). Only the plain data payload is propagated below; a
            //   stale aggregate head simply rebases again on the next reconciliation attempt.
            transitionContext.Data = value.LatestData.Data;

            // Re-basing the snapshot both clears the drained journal (fresh tracker) and lets
            // subsequent phases observe the live aggregate; the old snapshot is unreachable
            // afterwards (parallel branches copy their own snapshots), so no explicit
            // AcknowledgeDataChanges on it is needed.
            scriptContext.RefreshInstance(transitionContext.Instance);
        }

        transitionContext.ApplyScriptContextMutations(scriptContext);
        return Result.Ok();
    }
}
