using BBT.Aether.Results;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Data-only short-circuit for updateData against a parent with an open SubFlow correlation:
/// the freshly written data (CreateTransitionRecordStep, order 20) is all such a request may
/// do — it must not run tasks, must not re-schedule timers, must not evaluate auto transitions,
/// and must never forward to / restart the subflow. Skipping to Finalize completes the
/// transition record and leaves the instance status untouched (updateData never owns it).
/// Parents whose correlations are SubProcess (fire-and-forget) or already completed run the
/// full pipeline instead, so their own auto transitions are evaluated with the new data.
/// </summary>
public sealed class HandleUpdateDataDataOnlyStep : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.UpdateDataDataOnly;

    /// <inheritdoc />
    public Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.IsUpdateDataTransition() && context.Instance.HasActiveSubFlow)
        {
            return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.SkipToFinalize()));
        }

        return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork()));
    }
}
