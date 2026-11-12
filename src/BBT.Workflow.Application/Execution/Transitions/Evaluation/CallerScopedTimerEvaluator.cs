using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Tasks.Coordinator;

namespace BBT.Workflow.Execution.Evaluation;

/// <summary>
/// Evaluates a scheduled transition's timer script with the hop's caller exposed through
/// <see cref="ScriptContext.Caller"/>.
/// </summary>
public interface ICallerScopedTimerEvaluator
{
    /// <summary>
    /// Runs <paramref name="scheduledTransition"/>'s timer against <paramref name="scriptContext"/>, exposing the
    /// caller of the hop described by <paramref name="context"/> for the evaluation only.
    /// </summary>
    Task<Result<TimerSchedule>> EvaluateAsync(
        TransitionExecutionContext context,
        Transition scheduledTransition,
        ScriptContext scriptContext,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Caller roles (context.Caller) in timer scripts — council 2026-11-12-script-caller-roles-background (Chair
/// override): a timer script runs when its state is ENTERED (arming), not when the timer fires, and sees the roles
/// of the caller who drove that hop. A state entered by a System hop (a fired timer re-arming, a SubFlow resume, a
/// continuation) has no caller: the accessor is unavailable there, so a timer script that reads roles must guard
/// <c>IsAvailable</c> and choose a default. A role read the provider could not answer fails the step (retryable
/// <c>Authorization:110006</c>) rather than arming a timer from an unidentified caller's schedule.
/// </summary>
public sealed class CallerScopedTimerEvaluator(
    ITaskTimerService taskTimerService,
    ICallerAccessorFactory? callerAccessorFactory = null) : ICallerScopedTimerEvaluator
{
    /// <inheritdoc />
    public async Task<Result<TimerSchedule>> EvaluateAsync(
        TransitionExecutionContext context,
        Transition scheduledTransition,
        ScriptContext scriptContext,
        CancellationToken cancellationToken = default)
    {
        var caller = callerAccessorFactory?.ForHop(context.Actor, context.Headers, cancellationToken)
                     ?? NullCallerAccessor.Instance;
        var schedule = await ExecuteWithCallerAsync(scheduledTransition, scriptContext, caller, cancellationToken);

        return caller.ResolutionFailed
            ? Result<TimerSchedule>.Fail(WorkflowErrors.CallerRolesUnavailable())
            : schedule;
    }

    private async Task<Result<TimerSchedule>> ExecuteWithCallerAsync(
        Transition scheduledTransition,
        ScriptContext scriptContext,
        ICallerAccessor caller,
        CancellationToken cancellationToken)
    {
        using (scriptContext.ExposeCaller(caller))
        {
            return await taskTimerService.ExecuteTimerAsync(scheduledTransition.Timer!, scriptContext, cancellationToken);
        }
    }
}
