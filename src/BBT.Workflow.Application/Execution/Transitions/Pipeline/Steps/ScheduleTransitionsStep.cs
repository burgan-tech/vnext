using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that schedules future transitions based on timers.
/// Enqueues scheduled transitions for later execution.
/// Uses Result pattern for exception-free error handling.
/// Cancels existing timer jobs before scheduling new ones to handle self-transitions correctly.
/// </summary>
public sealed class ScheduleTransitionsStep(
    IBackgroundJobService backgroundJobService,
    IJobScheduler jobScheduler,
    ITaskTimerService taskTimerService,
    IScriptContextFactory scriptContextFactory,
    IInstanceJobRepository jobRepository,
    IInstanceRepository instanceRepository,
    ILogger<ScheduleTransitionsStep> logger,
    IRuntimeInfoProvider runtimeInfoProvider) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.Schedule;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(ScheduleTransitionsStep)}");

        // Skip if no scheduled transitions
        if (!HasScheduledTransitions(context))
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Cancel existing timer jobs for scheduled transitions that will be re-scheduled
        // This handles self-transitions and re-entry scenarios where timers need to reset
        var cancelResult = await CancelExistingScheduledJobsAsync(context, cancellationToken);
        if (!cancelResult.IsSuccess)
        {
            return Result<StepOutcome>.Fail(cancelResult.Error);
        }

        // Process each scheduled transition
        foreach (var scheduledTransition in context.Target!.ScheduledTransitions)
        {
            var result = await ScheduleTransitionAsync(context, scheduledTransition, cancellationToken);
            if (!result.IsSuccess)
            {
                return Result<StepOutcome>.Fail(result.Error);
            }
        }

        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    /// <summary>
    /// Checks if context has scheduled transitions.
    /// </summary>
    private static bool HasScheduledTransitions(TransitionExecutionContext context)
        => context.Target?.ScheduledTransitions != null && context.Target.ScheduledTransitions.Any();

    /// <summary>
    /// Cancels existing timer jobs for scheduled transitions that will be re-scheduled.
    /// This handles self-transitions and re-entry scenarios where timers need to reset.
    /// </summary>
    private async Task<Result> CancelExistingScheduledJobsAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var activeJobs = await jobRepository.GetListActiveAsync(context.InstanceId, cancellationToken);
        
        if (!activeJobs.Any())
        {
            return Result.Ok();
        }

        foreach (var job in activeJobs)
        {
            try
            {
                await jobScheduler.DeleteAsync(
                    TransitionTimerJobHandler.HandlerName, 
                    job.JobName, 
                    cancellationToken);
                
                job.MarkAsProcessed();
                await jobRepository.UpdateAsync(job, true, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, 
                    "Failed to cancel timer job: JobName={JobName}",
                    job.JobName);
            }
        }

        logger.ExistingTimerJobsCanceled(activeJobs.Count, context.InstanceId);

        return Result.Ok();
    }

    /// <summary>
    /// Builds the job name for a scheduled transition.
    /// </summary>
    private static string BuildJobName(Guid instanceId, string scheduledTransitionKey)
        => $"trans-{instanceId}-{scheduledTransitionKey}";

    /// <summary>
    /// Schedules a single transition for future execution using Railway chain.
    /// </summary>
    private async Task<Result> ScheduleTransitionAsync(
        TransitionExecutionContext context,
        Transition scheduledTransition,
        CancellationToken cancellationToken)
    {
        // Validate timer exists
        if (scheduledTransition.Timer == null)
        {
            logger.TransitionTimerSkipped(scheduledTransition.Key);
            return Result.Ok(); // Skip, not an error
        }

        // Railway chain: Build context -> Evaluate timer -> Build payload -> Enqueue -> Persist
        return await Result.Ok(scheduledTransition)
            .MapAsync(transition => BuildScriptContextAsync(context, transition, cancellationToken))
            .BindAsync(scriptContext => EvaluateTimerAsync(scheduledTransition, scriptContext, cancellationToken))
            .Map(timerSchedule => BuildSchedulingInfo(context, scheduledTransition, timerSchedule))
            .ThenAsync(info => EnqueueAndPersistAsync(info, cancellationToken));
    }

    /// <summary>
    /// Builds script context for timer evaluation.
    /// </summary>
    private async Task<ScriptContext> BuildScriptContextAsync(
        TransitionExecutionContext context,
        Transition scheduledTransition,
        CancellationToken cancellationToken)
    {
        return await scriptContextFactory
            .NewBuilder(instanceRepository)
            .WithWorkflow(context.Workflow)
            .WithInstance(context.Instance)
            .WithRuntime(runtimeInfoProvider)
            .WithTransition(scheduledTransition)
            .WithHeaders(context.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .WithRouteValues(context.RouteValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .BuildAsync(cancellationToken);
    }

    /// <summary>
    /// Evaluates timer to get the schedule.
    /// </summary>
    private async Task<Result<TimerSchedule>> EvaluateTimerAsync(
        Transition scheduledTransition,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        return await taskTimerService.ExecuteTimerAsync(
            scheduledTransition.Timer!,
            scriptContext,
            cancellationToken);
    }

    /// <summary>
    /// Builds scheduling info from context and timer schedule.
    /// </summary>
    private static TransitionSchedulingInfo BuildSchedulingInfo(
        TransitionExecutionContext context,
        Transition scheduledTransition,
        TimerSchedule timerSchedule)
    {
        var jobName = $"trans-{context.InstanceId}-{context.Transition?.Key}";
        var activity = Activity.Current;
        
        var payload = new TransitionTimerPayload
        {
            JobName = jobName,
            Domain = context.Domain,
            FlowName = context.WorkflowKey,
            Version = context.Workflow.Version,
            TransitionKey = scheduledTransition.Key,
            InstanceId = context.InstanceId,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString
        };

        var metadata = new Dictionary<string, object>
        {
            ["domain"] = context.Domain,
            ["flowName"] = context.WorkflowKey,
            ["instanceId"] = context.InstanceId.ToString()
        };

        return new TransitionSchedulingInfo(
            context,
            jobName,
            payload,
            timerSchedule.ToDaprJobSchedule().ExpressionValue,
            metadata);
    }

    /// <summary>
    /// Enqueues the job and persists the instance job record.
    /// </summary>
    private async Task<Result> EnqueueAndPersistAsync(
        TransitionSchedulingInfo info,
        CancellationToken cancellationToken)
    {
        var jobId = await backgroundJobService.EnqueueAsync(
            TransitionTimerJobHandler.HandlerName,
            info.JobName,
            info.Payload,
            info.ScheduleExpression,
            metadata: info.Metadata,
            cancellationToken);

        await jobRepository.InsertAsync(
            InstanceJob.Create(
                jobId,
                info.JobName,
                jobId,
                info.Context.Domain,
                info.Context.WorkflowKey,
                info.Context.InstanceId),
            true,
            cancellationToken);

        logger.LogInformation(
            "Scheduled timer job: JobId={JobId}, JobName={JobName}, InstanceId={InstanceId}",
            jobId,
            info.JobName,
            info.Context.InstanceId);

        return Result.Ok();
    }

    /// <summary>
    /// Encapsulates transition scheduling information.
    /// </summary>
    private sealed record TransitionSchedulingInfo(
        TransitionExecutionContext Context,
        string JobName,
        TransitionTimerPayload Payload,
        string ScheduleExpression,
        Dictionary<string, object> Metadata);
}
