using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
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

        // Cheap pre-check: skip work when the instance is no longer awaiting acknowledge
        // (already resumed by the other trigger). The pipeline-level guard in
        // ClearBusyOnResumeStep is the authoritative idempotency check under the reserved lock.
        var instance = await instanceRepository.FindAsync(p => p.Id == instanceId, false, cancellationToken);
        if (instance is null)
        {
            logger.InstanceNotFound(instanceId, flowKey);
            activity?.SetTag(TelemetryConstants.TagNames.LongPollAckResumeOutcome, "instance_not_found");
            return Result.Ok();
        }

        if (!instance.IsAwaitingLongPollAck)
        {
            logger.LongPollAckResumeSkipped(instanceId);
            activity?.SetTag(TelemetryConstants.TagNames.LongPollAckResumeOutcome, "not_awaiting");
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
            return Result.Fail(result.Error);
        }

        activity?.SetTag(TelemetryConstants.TagNames.LongPollAckResumeOutcome, "resumed");
        logger.LongPollAckResumed(instanceId);
        return Result.Ok();
    }
}
