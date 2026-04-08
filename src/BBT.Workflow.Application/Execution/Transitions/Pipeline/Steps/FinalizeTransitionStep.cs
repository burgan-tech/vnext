using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that finalizes the transition execution.
/// Updates the transition record and performs cleanup operations.
/// </summary>
public sealed class FinalizeTransitionStep(
    IInstanceTransitionRepository instanceTransitionRepository,
    IWorkflowMetrics workflowMetrics) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.Finalize;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(FinalizeTransitionStep)}");
        
        var recordId = GetTransitionRecordId(context);
        
        if (recordId != Guid.Empty)
        {
            // Railway chain: Load -> Complete -> Record metric -> Persist
            await Result.Ok(recordId)
                .BindAsync(id => LoadTransitionRecordAsync(id, cancellationToken))
                .Tap(transition => transition?.Completed(context.Instance.GetCurrentState))
                .Tap(transition => RecordDurationMetricIfAvailable(context, transition))
                .TapAsync(transition => UpdateTransitionIfExistsAsync(transition, cancellationToken));
        }

        PerformCleanup(context);

        return Result<StepOutcome>.Ok(StepOutcome.Continue());
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
    /// Loads the transition record from repository.
    /// </summary>
    private async Task<Result<InstanceTransition?>> LoadTransitionRecordAsync(
        Guid recordId, 
        CancellationToken cancellationToken)
    {
        var transition = await instanceTransitionRepository.GetAsync(recordId, true, cancellationToken);
        return Result<InstanceTransition?>.Ok(transition);
    }

    /// <summary>
    /// Records duration metric if available.
    /// </summary>
    private void RecordDurationMetricIfAvailable(TransitionExecutionContext context, InstanceTransition? transition)
    {
        if (transition?.Duration.HasValue == true)
        {
            workflowMetrics.RecordStateDuration(
                context.Workflow.Key,
                transition.FromState,
                transition.Duration.Value.TotalSeconds);
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
