using BBT.Aether.Results;

namespace BBT.Workflow.Execution.LongPoll;

/// <summary>
/// Resumes a transition pipeline that was paused for declarative long-poll termination on state entry.
/// Invoked by the acknowledge endpoint and by the fallback timeout job. Resuming continues the
/// epilogue (Schedule → Auto → Finish → Finalize) from where the pause stopped and clears the
/// instance's long-poll acknowledge marker. The operation is idempotent: a resume on an instance
/// that is no longer awaiting acknowledge is a safe no-op.
/// </summary>
public interface ILongPollAckResumeService
{
    /// <summary>
    /// Resumes the paused pipeline for the given instance.
    /// </summary>
    /// <param name="domain">The workflow domain.</param>
    /// <param name="flowKey">The workflow key.</param>
    /// <param name="flowVersion">The workflow version.</param>
    /// <param name="instanceId">The paused instance identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Ok on success or no-op; Fail when the resume pipeline fails.</returns>
    Task<Result> ResumeAsync(
        string domain,
        string flowKey,
        string? flowVersion,
        Guid instanceId,
        CancellationToken cancellationToken = default);
}
