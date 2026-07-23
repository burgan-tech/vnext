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
/// that service), synchronizes the persisted rows onto the live aggregate, refreshes the script
/// context snapshot, and only then applies instance mutations. On reconciliation failure the
/// journal and mutations are intentionally left untouched.
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

        var changeSet = scriptContext.Instance?.GetPendingDataChangeSet();
        if (changeSet is not null)
        {
            var reconciled = await reconciliationService.ApplyAsync(
                transitionContext.Instance, changeSet, cancellationToken);
            if (!reconciled.IsSuccess)
            {
                return Result.Fail(reconciled.Error);
            }

            var value = reconciled.Value!;
            transitionContext.Instance.SynchronizePartiallyLoadedData(
                value.AppendedData.Count == 0 ? [value.LatestData] : value.AppendedData);
            transitionContext.Data = value.LatestData.Data;
            scriptContext.Instance!.AcknowledgeDataChanges(value.LatestData);
            scriptContext.RefreshInstance(transitionContext.Instance);
        }

        transitionContext.ApplyScriptContextMutations(scriptContext);
        return Result.Ok();
    }
}
