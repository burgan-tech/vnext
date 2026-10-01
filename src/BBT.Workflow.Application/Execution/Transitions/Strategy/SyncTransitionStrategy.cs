using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using System.Diagnostics;
using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Strategies;

/// <summary>
/// Synchronous transition execution strategy.
/// Executes transitions immediately in the current thread/context.
/// Delegates lock management and sync dispatch chain to TransitionPipeline.
/// Validation is handled by TransitionPipeline guard.
/// </summary>
public sealed class SyncTransitionStrategy(
    TransitionPipeline pipeline) : ITransitionStrategy
{
    /// <summary>Span name, unchanged from the one Aether's aspect derived from this method.</summary>
    internal const string OperationName = "SyncTransitionStrategy.ExecuteAsync";

    public ExecMode Mode => ExecMode.Sync;
     
    /// <inheritdoc />
    /// <summary>
    /// Executes transition synchronously.
    /// Pipeline handles validation, context creation, locking, and sync dispatch chain.
    /// </summary>
    /// <remarks>
    /// Owns its span explicitly rather than through Aether's <c>[Trace]</c> aspect, which marks
    /// every normal return <c>Ok</c> and so overwrote the error this method records from the
    /// pipeline's <see cref="Result{T}"/>. The display name is kept for query continuity.
    /// </remarks>
    public async Task<Result<TransitionExecutionContext>> ExecuteAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        using var activity = PipelineStepActivityHelper.StartTransitionActivity(
            OperationName, context.TransitionKey);

        // Pipeline handles: validation guard, context creation, lock, steps, sync dispatch chain
        var pipelineResult = await pipeline.RunAsync(context, cancellationToken);
        
        if (!pipelineResult.IsSuccess)
        {
            SetActivityError(activity, pipelineResult.Error);
            return pipelineResult;
        }

        var ctx = pipelineResult.Value!;
        EnrichTelemetry(activity, ctx);
        SetActivityStatus(activity, pipelineResult);

        return pipelineResult;
    }

    /// <summary>
    /// Enriches the activity with telemetry tags.
    /// Pure side effect - doesn't affect flow.
    /// </summary>
    private static void EnrichTelemetry(
        Activity? activity,
        TransitionExecutionContext ctx)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Domain, ctx.Workflow.Domain);
        activity.SetBaggage(TelemetryConstants.TagNames.Domain, ctx.Workflow.Domain);
    }

    /// <summary>
    /// Sets activity status based on result.
    /// </summary>
    private static void SetActivityStatus<T>(Activity? activity, Result<T> result)
    {
        if (activity is null) return;

        if (result.IsSuccess)
        {
            // A faulted pipeline returns success but has already marked this span Error.
            activity.SetOkUnlessError();
        }
        else
        {
            SetActivityError(activity, result.Error);
        }
    }

    /// <summary>
    /// Sets activity error status with error details.
    /// </summary>
    private static void SetActivityError(Activity? activity, Error error)
    {
        if (activity is null) return;

        activity.SetResultError(error.Code, error.Message);
    }
}