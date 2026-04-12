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

        // Skip for SubFlow resume - state cleared/managed by ClearBusyOnResumeStep
        if (context.Directives.IsSubFlowResume)
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Timeout: target state was pre-resolved in ApplyTimeoutStateStep; apply it here
        // so that CancelScheduledJobs (39) and OnExit (40) ran against the correct current state first
        if (context.Directives.IsTimeoutTransition)
        {
            return await ApplyTimeoutStateChangeAsync(context, cancellationToken);
        }

        // Skip if no transition (other non-timeout resume scenarios)
        if (context.Transition == null)
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway Oriented Programming: Fluent chain
        return await Result.Ok(BuildStateTransitionInfo(context))
            .Tap(info => RecordTransitionMetric(context, info))
            .BindAsync(info => PerformStateChangeAsync(context, info, cancellationToken)) 
            .ThenAsync(_ => UpdateTargetStateInContext(context))
            .OnSuccess(_ => RecordStateEntryMetric(context))
            .OnSuccess(_ => LogStateChange(context))
            .OnSuccess(_ => AddTelemetryEvent(context))
            .MapAsync(_ => StepOutcome.Continue());
    }

    /// <summary>
    /// Applies the timeout state change using the target state pre-resolved by ApplyTimeoutStateStep.
    /// Mirrors the normal PerformStateChangeAsync + UpdateTargetStateInContext flow.
    /// </summary>
    private async Task<Result<StepOutcome>> ApplyTimeoutStateChangeAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.Target == null)
        {
            return Result<StepOutcome>.Fail(WorkflowErrors.TimeoutConfigMissing(context.Workflow.Key));
        }

        var fromState = context.Instance.GetCurrentState;

        context.Instance.ChangeState(context.Target);
        await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

        // Sync context to reflect the applied state (mirrors UpdateTargetStateInContext)
        context.Current = context.Target;

        RecordStateEntryMetric(context);

        Activity.Current?.AddEvent(new ActivityEvent("state.changed",
            tags: new ActivityTagsCollection
            {
                { TelemetryConstants.TagNames.StateFrom, fromState },
                { TelemetryConstants.TagNames.StateTo, context.Target.Key }
            }));

        return Result<StepOutcome>.Ok(StepOutcome.Continue());
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
    /// Delegates well-known state key resolution to the Workflow aggregate.
    /// Uses Result Pattern for error handling without throwing exceptions.
    /// </summary>
    private async Task<Result<StateTransitionInfo>> PerformStateChangeAsync(
        TransitionExecutionContext context,
        StateTransitionInfo info,
        CancellationToken cancellationToken)
    {
        // Resolve target state through workflow aggregate (handles $self and other well-known keys)
        var stateResult = context.Workflow.GetState(info.Transition.Target, context.Instance.GetCurrentState);
        
        if (!stateResult.IsSuccess)
        {
            return Result<StateTransitionInfo>.Fail(stateResult.Error);
        }
        
        context.Instance.ChangeState(stateResult.Value!);
        await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
        
        return Result<StateTransitionInfo>.Ok(info);
    }

    /// <summary>
    /// Updates target state in context using Result Pattern.
    /// Synchronizes context.Current and context.Target with the instance's actual current state.
    /// This is called after state change to reflect the new state in the context.
    /// </summary>
    private Task<Result<TransitionExecutionContext>> UpdateTargetStateInContext(TransitionExecutionContext context)
    {
        // After state change, update context to reflect the actual current state
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
