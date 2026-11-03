using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.LongPoll;

/// <summary>
/// Resumes a transition pipeline paused for declarative long-poll termination on state entry.
/// Mirrors the SubFlow-completion resume: enters the pipeline in <see cref="ExecMode.Resume"/> from
/// <see cref="LifecycleOrder.ClearBusyOnResumeStep"/> with <see cref="ExecutionInfo.IsLongPollAckResume"/>,
/// which clears the acknowledge marker, clears Busy, and runs the remaining epilogue steps.
/// </summary>
public sealed class LongPollAckResumeService(
    IInstanceRepository instanceRepository,
    IWorkflowExecutionService workflowExecutionService,
    ILogger<LongPollAckResumeService> logger) : ILongPollAckResumeService
{
    /// <inheritdoc />
    public async Task<Result> ResumeAsync(
        string domain,
        string flowKey,
        string? flowVersion,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        // Envelope for the pre-check load plus the resume: only the pipeline's own spans appeared
        // before, so an ack that was skipped as already-resumed left no trace of its decision.
        using var activity = PipelineStepActivityHelper.StartOperationActivity("LongPoll.AckResume");
        activity?.SetTag(TelemetryConstants.TagNames.InstanceId, instanceId.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.Flow, flowKey);
        activity?.SetTag(TelemetryConstants.TagNames.Domain, domain);

        // Who asked for the resume. The ack-timeout job classifies its episode before calling here;
        // an HTTP ack arrives with the middleware's `http` seed (UseEpisode below names it `ack`).
        // The span tag, the counter and the log line all read this one value, so "the client never
        // acknowledges" is visible on every surface and not only in the episode span.
        var trigger = WorkflowTraceLane.Episode?.Trigger == TelemetryConstants.ActivationTriggers.AckTimeout
            ? TelemetryConstants.ActivationTriggers.AckTimeout
            : TelemetryConstants.ActivationTriggers.Ack;
        activity?.SetTag(TelemetryConstants.TagNames.ActivationTrigger, trigger);

        // Cheap pre-check: skip work when the instance is no longer awaiting acknowledge
        // (already resumed by the other trigger). The pipeline-level guard in
        // ClearBusyOnResumeStep is the authoritative idempotency check under the reserved lock.
        var instance = await instanceRepository.FindAsync(p => p.Id == instanceId, false, cancellationToken);
        if (instance is null)
        {
            logger.InstanceNotFound(instanceId, flowKey);
            RecordOutcome(activity, domain, flowKey, trigger, "instance_not_found");
            return Result.Ok();
        }

        if (!instance.IsAwaitingLongPollAck)
        {
            logger.LongPollAckResumeSkipped(instanceId);
            RecordOutcome(activity, domain, flowKey, trigger, "not_awaiting");
            return Result.Ok();
        }

        var input = new WorkflowExecutionContext
        {
            Domain = domain,
            WorkflowKey = flowKey,
            WorkflowVersion = flowVersion,
            InstanceId = instanceId.ToString(),
            TransitionKey = string.Empty, // logging only — internal resume has no transition
            TriggerType = TriggerType.Manual,
            Mode = ExecMode.Resume,
            CallerMode = ExecMode.Async,
            Headers = new Dictionary<string, string?>(),
            Actor = ExecutionActor.System,
            RequestedAt = DateTimeOffset.UtcNow,
            Execution = new ExecutionInfo
            {
                ExecutionChainId = Guid.NewGuid().ToString("N"),
                ChainDepth = 0,
                ResumeFrom = LifecycleOrder.ClearBusyOnResumeStep,
                IsLongPollAckResume = true
            }
        };

        // An ack over HTTP opens an `ack` episode; when the ack-timeout job drives this resume it
        // has already classified the episode as `ack-timeout`, which UseEpisode leaves alone.
        using var episode = WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.Ack, transitionKey: null);

        var result = await workflowExecutionService.ExecuteTransitionAsync(input, cancellationToken);
        if (!result.IsSuccess)
        {
            logger.LongPollAckResumeFailed(instanceId, result.Error.Message ?? result.Error.Code);
            activity.SetResultError(result.Error.Code, result.Error.Message);
            RecordOutcome(activity, domain, flowKey, trigger, "failed");
            return Result.Fail(result.Error);
        }

        RecordOutcome(activity, domain, flowKey, trigger, "resumed");
        if (trigger == TelemetryConstants.ActivationTriggers.AckTimeout)
            logger.LongPollFallbackResumed(instanceId, flowKey);
        else
            logger.LongPollAckResumed(instanceId);
        return Result.Ok();
    }

    private static void RecordOutcome(Activity? activity, string domain, string flowKey, string trigger, string outcome)
    {
        activity?.SetTag(TelemetryConstants.TagNames.LongPollAckResumeOutcome, outcome);
        WorkflowMetrics.LongPollResumes.Add(1,
            new KeyValuePair<string, object?>(TelemetryConstants.TagNames.Domain, domain),
            new KeyValuePair<string, object?>(TelemetryConstants.TagNames.Flow, flowKey),
            new KeyValuePair<string, object?>(TelemetryConstants.TagNames.ActivationTrigger, trigger),
            new KeyValuePair<string, object?>(TelemetryConstants.TagNames.LongPollAckResumeOutcome, outcome));
    }
}
