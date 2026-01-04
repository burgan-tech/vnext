using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Handlers;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Strategies;

/// <summary>
/// Synchronous transition execution strategy.
/// Executes transitions immediately in the current thread/context.
/// Delegates lock management and sync dispatch chain to TransitionPipeline.
/// </summary>
public sealed class SyncTransitionStrategy(
    ITransitionHandlerFactory handlerFactory,
    TransitionPipeline pipeline) : ITransitionStrategy
{
    public ExecMode Mode => ExecMode.Sync;
     
    /// <inheritdoc />
    /// <summary>
    /// Executes transition synchronously.
    /// Railway chain: Resolve Handler → Execute Pipeline (with lock and sync dispatch chain)
    /// </summary>
    [Trace]
    public Task<Result<TransitionExecutionContext>> ExecuteAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        var activity = Activity.Current;

        return handlerFactory.Get(context.TriggerType)
            .BindAsync(handler => ExecuteWithPipelineAsync(handler, context, activity, cancellationToken));
    }

    /// <summary>
    /// Executes the handler lifecycle with pipeline.
    /// Pipeline now handles context creation, locking, and sync dispatch chain.
    /// </summary>
    private async Task<Result<TransitionExecutionContext>> ExecuteWithPipelineAsync(
        ITransitionHandler handler,
        WorkflowExecutionContext context,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        // Pipeline handles: context creation, lock, steps, sync dispatch chain
        var pipelineResult = await pipeline.RunAsync(context, cancellationToken);
        
        if (!pipelineResult.IsSuccess)
        {
            SetActivityError(activity, pipelineResult.Error);
            return pipelineResult;
        }

        var ctx = pipelineResult.Value!;
        EnrichTelemetry(activity, ctx, handler, context.TriggerType);
        SetActivityStatus(activity, pipelineResult);

        return pipelineResult;
    }

    /// <summary>
    /// Enriches the activity with telemetry tags.
    /// Pure side effect - doesn't affect flow.
    /// </summary>
    private static void EnrichTelemetry(
        Activity? activity,
        TransitionExecutionContext ctx,
        ITransitionHandler handler,
        TriggerType triggerType)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Domain, ctx.Workflow.Domain);
        activity.SetTag(TelemetryConstants.TagNames.HandlerName, handler.GetType().Name);

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
            activity.SetStatus(ActivityStatusCode.Ok);
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

        activity.SetStatus(ActivityStatusCode.Error, error.Message);
        activity.AddTag("error.code", error.Code);
    }
}