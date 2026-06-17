using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Dapr.Jobs.Models;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step for declarative long-poll termination on state entry.
/// When the entered state declares <c>interaction.longPoll.terminate</c>, this step arms a durable
/// acknowledge marker, schedules a fallback resume job, and pauses the pipeline before the epilogue
/// (Schedule/Auto) by skipping to Finalize — the instance stays Busy until the client acknowledges
/// (or the fallback timeout fires), at which point the pipeline resumes via
/// <see cref="ILongPollAckResumeService"/>.
/// </summary>
public sealed class HandleLongPollTerminationStep(
    IInstanceRepository instanceRepository,
    IInstanceJobRepository jobRepository,
    IBackgroundJobService backgroundJobService,
    IGuidGenerator guidGenerator,
    ILogger<HandleLongPollTerminationStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.LongPollTermination;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(HandleLongPollTerminationStep)}");

        if (!IsApplicable(context))
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        var token = guidGenerator.Create();
        context.Instance.ArmLongPollAck(token);
        await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

        await ScheduleFallbackAsync(context, token, cancellationToken);

        logger.LongPollTerminationArmed(
            context.InstanceId,
            context.Target!.Key,
            context.Target.LongPollFallbackTimeoutSeconds);

        // Pause: skip the epilogue (Schedule/Auto) and go straight to Finalize so the transition
        // record completes. The instance stays Busy; resume happens on acknowledge or fallback.
        return Result<StepOutcome>.Ok(new StepOutcome
        {
            MutateDirectives = d =>
            {
                d.RequestEpilogue(EpilogueMode.Skip);
                d.MarkTerminal();
            },
            SkipToOrder = LifecycleOrder.Finalize
        });
    }

    /// <summary>
    /// Applicable when the entered state terminates long polling and the pipeline is not itself a
    /// long-poll resume, and the instance is not already awaiting an acknowledge (idempotent re-entry).
    /// </summary>
    private static bool IsApplicable(TransitionExecutionContext context)
        => context.Target?.TerminatesLongPollOnEntry == true
           && !context.Directives.IsLongPollAckResume
           && !context.Instance.IsAwaitingLongPollAck;

    /// <summary>
    /// Schedules the one-shot fallback resume job and tracks it so acknowledge can cancel it.
    /// The job name carries the well-known key suffix so the existing transition-key cancellation
    /// path can target it.
    /// </summary>
    private async Task ScheduleFallbackAsync(
        TransitionExecutionContext context,
        Guid token,
        CancellationToken cancellationToken)
    {
        var jobName = $"lpack-{context.InstanceId}-{LongPollAckConstants.JobKey}";
        var activity = Activity.Current;

        var payload = new LongPollAckTimeoutPayload
        {
            JobName = jobName,
            Domain = context.Domain,
            InstanceId = context.InstanceId,
            FlowName = context.WorkflowKey,
            Version = context.Workflow.Version,
            AckToken = token,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString
        };

        var schedule = DaprJobSchedule
            .FromDateTime(DateTime.UtcNow.AddSeconds(context.Target!.LongPollFallbackTimeoutSeconds))
            .ExpressionValue;

        var metadata = new Dictionary<string, object>
        {
            ["domain"] = context.Domain,
            ["flowName"] = context.WorkflowKey,
            ["instanceId"] = context.InstanceId.ToString()
        };

        var jobId = await backgroundJobService.EnqueueAsync(
            LongPollAckTimeoutJobHandler.HandlerName,
            jobName,
            payload,
            schedule,
            metadata,
            cancellationToken: cancellationToken);

        await jobRepository.InsertAsync(
            InstanceJob.Create(
                jobId,
                jobName,
                jobId,
                context.Domain,
                context.WorkflowKey,
                context.InstanceId),
            true,
            cancellationToken);
    }
}
