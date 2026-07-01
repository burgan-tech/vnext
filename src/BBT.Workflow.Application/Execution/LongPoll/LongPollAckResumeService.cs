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
        // Cheap pre-check: skip work when the instance is no longer awaiting acknowledge
        // (already resumed by the other trigger). The pipeline-level guard in
        // ClearBusyOnResumeStep is the authoritative idempotency check under the reserved lock.
        var instance = await instanceRepository.FindAsync(p => p.Id == instanceId, false, cancellationToken);
        if (instance is null)
        {
            logger.InstanceNotFound(instanceId, flowKey);
            return Result.Ok();
        }

        if (!instance.IsAwaitingLongPollAck)
        {
            logger.LongPollAckResumeSkipped(instanceId);
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

        var result = await workflowExecutionService.ExecuteTransitionAsync(input, cancellationToken);
        if (!result.IsSuccess)
        {
            logger.LongPollAckResumeFailed(instanceId, result.Error.Message ?? result.Error.Code);
            return Result.Fail(result.Error);
        }

        logger.LongPollAckResumed(instanceId);
        return Result.Ok();
    }
}
