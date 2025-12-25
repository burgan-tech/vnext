using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that performs the core state transition.
/// This step changes the instance state and updates the target state in context.
/// </summary>
public sealed class ChangeStateStep(
    IInstanceRepository instanceRepository,
    IWorkflowMetrics workflowMetrics,
    ILogger<ChangeStateStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.ChangeState;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(ChangeStateStep)}");

        // Skip for SubFlow resume - state already changed
        if (context.Directives.IsSubFlowResume || context.Transition == null)
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway Oriented Programming: Fluent chain
        return await Result.Ok(BuildStateTransitionInfo(context))
            .Tap(info => RecordTransitionMetric(context, info))
            .TapAsync(info => PerformStateChangeAsync(context, info, cancellationToken))
            .ThenAsync(_ => UpdateTargetStateInContext(context))
            .OnSuccess(_ => RecordStateEntryMetric(context))
            .OnSuccess(_ => LogStateChange(context))
            .OnSuccess(_ => AddTelemetryEvent(context))
            .MapAsync(_ => StepOutcome.Continue());
    }

    /// <summary>
    /// Builds state transition information from context.
    /// </summary>
    private static StateTransitionInfo BuildStateTransitionInfo(TransitionExecutionContext context)
    {
        return new StateTransitionInfo(
            context.Instance.GetCurrentState,
            context.Transition!.Target,
            context.Transition);
    }

    /// <summary>
    /// Records state transition metric.
    /// </summary>
    private void RecordTransitionMetric(TransitionExecutionContext context, StateTransitionInfo info)
    {
        workflowMetrics.RecordStateTransition(
            context.Workflow.Key,
            info.FromState,
            info.ToState);
    }

    /// <summary>
    /// Performs the actual state change and updates the instance in repository.
    /// </summary>
    private async Task PerformStateChangeAsync(
        TransitionExecutionContext context,
        StateTransitionInfo info,
        CancellationToken cancellationToken)
    {
        context.Instance.ChangeState(info.Transition);
        await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
    }

    /// <summary>
    /// Updates target state in context using Result Pattern.
    /// Returns Result because GetState can legitimately fail (state not found).
    /// </summary>
    private Task<Result<TransitionExecutionContext>> UpdateTargetStateInContext(TransitionExecutionContext context)
    {
        var result = context.Workflow.GetState(context.Instance.GetCurrentState)
            .Map(state =>
            {
                context.Current = state;
                context.Target = state;
                return context;
            });
        return Task.FromResult(result);
    }

    /// <summary>
    /// Records state entry metric.
    /// </summary>
    private void RecordStateEntryMetric(TransitionExecutionContext context)
    {
        workflowMetrics.RecordStateEntry(
            context.Workflow.Key,
            context.Instance.GetCurrentState);
    }

    /// <summary>
    /// Logs state change with structured logging.
    /// </summary>
    private void LogStateChange(TransitionExecutionContext context)
    {
        var fromState = context.Instance.GetCurrentState;
        var toState = context.Target?.Key ?? "unknown";
        logger.StateChanged(fromState, toState, context.InstanceId);
    }

    /// <summary>
    /// Adds state changed event to telemetry span.
    /// </summary>
    private static void AddTelemetryEvent(TransitionExecutionContext context)
    {
        Activity.Current?.AddEvent(new ActivityEvent("state.changed",
            tags: new ActivityTagsCollection
            {
                { TelemetryConstants.TagNames.StateFrom, context.Instance.GetCurrentState },
                { TelemetryConstants.TagNames.StateTo, context.Target?.Key ?? "unknown" }
            }));
    }

    /// <summary>
    /// Encapsulates state transition information.
    /// </summary>
    private sealed record StateTransitionInfo(
        string FromState,
        string ToState,
        Definitions.Transition Transition);
}
