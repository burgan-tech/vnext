using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that finalizes the transition execution.
/// Updates the transition record and performs cleanup operations.
/// </summary>
public sealed class FinalizeTransitionStep(
    IInstanceTransitionRepository instanceTransitionRepository) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.Finalize;

    /// <inheritdoc />
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        
        var recordId = GetTransitionRecordId(context);
        
        if (recordId != Guid.Empty)
        {
            // Prefer the record created/reused earlier in this pipeline. Resume and recovery
            // paths may not carry it, so retain the repository read as a fallback.
            await Result.Ok(recordId)
                .BindAsync(id => ResolveTransitionRecordAsync(context, id, cancellationToken))
                .Tap(transition => transition?.Completed(
                    context.Instance.GetCurrentState,
                    context.Instance.EffectiveState,
                    context.Instance.EffectiveStateType,
                    context.Instance.EffectiveStateSubType,
                    context.Instance.Stage))
                .Tap(transition => RecordDurationMetricIfAvailable(context, transition))
                .TapAsync(transition => UpdateTransitionIfExistsAsync(transition, cancellationToken));
        }

        ResolveIncidentOnSuccessfulErrorBoundaryTransition(context);

        PerformCleanup(context);

        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    /// <summary>
    /// If this pipeline run was an error-boundary transition and it completed successfully
    /// (no new fault), resolve the active incident that triggered it.
    /// </summary>
    private static void ResolveIncidentOnSuccessfulErrorBoundaryTransition(TransitionExecutionContext context)
    {
        if (!context.IsErrorBoundaryTransition)
            return;

        if (context.Instance.Status.Equals(Instances.InstanceStatus.Faulted))
            return;

        context.Instance.ResolveActiveIncident();
    }

    /// <summary>
    /// Gets the transition record ID from context items.
    /// </summary>
    private static Guid GetTransitionRecordId(TransitionExecutionContext context)
    {
        return context.Items.TryGetValue(WellKnownItems.TransitionRecordId, out var record) && record is Guid recordId
            ? recordId
            : Guid.Empty;
    }

    /// <summary>
    /// Resolves the transition record already carried by the pipeline, falling back to a
    /// read-only repository lookup for resume/recovery paths that only carry its identifier.
    /// </summary>
    private async Task<Result<InstanceTransition?>> ResolveTransitionRecordAsync(
        TransitionExecutionContext context,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        if (context.Items.TryGetValue(WellKnownItems.InstanceTransition, out var value)
            && value is InstanceTransition carriedTransition
            && carriedTransition.Id == recordId)
        {
            return Result<InstanceTransition?>.Ok(carriedTransition);
        }

        // Read-only on purpose: Completed() below only computes the values UpdateCompletedAsync
        // writes set-based. A tracked load made the ambient UoW's later flush rewrite the same
        // row a second time on every transition.
        var loadedTransition = await instanceTransitionRepository.FindAsReadOnlyAsync(recordId, cancellationToken);
        return Result<InstanceTransition?>.Ok(loadedTransition);
    }

    /// <summary>
    /// Records duration metric if available.
    /// </summary>
    private void RecordDurationMetricIfAvailable(TransitionExecutionContext context, InstanceTransition? transition)
    {
        if (transition?.Duration.HasValue == true)
        {
        }
    }

    /// <summary>
    /// Updates transition record if it exists.
    /// </summary>
    private async Task UpdateTransitionIfExistsAsync(InstanceTransition? transition, CancellationToken cancellationToken)
    {
        if (transition != null)
        {
            await instanceTransitionRepository.UpdateCompletedAsync(transition, cancellationToken);
        }
    }

    /// <summary>
    /// Performs cleanup operations.
    /// </summary>
    private static void PerformCleanup(TransitionExecutionContext context)
    {
        if (context.Cache.TryGetValue("ScriptContext", out var scriptContextObj) &&
            scriptContextObj is ScriptContext scriptContext)
        {
            scriptContext.Dispose();
        }

        context.ClearCacheForFinalize();
    }
}
