using BBT.Workflow.BackgroundJobs.Payloads;

namespace BBT.Workflow.BackgroundJobs.Recovery;

public interface IJobTimeoutRecoveryService
{
    /// <summary>
    /// Recovers an instance stuck in Busy status after a job execution timeout or cancellation.
    /// Faults the instance, records an incident, and closes any open transition record.
    /// </summary>
    Task FaultInstanceAsync(TransitionJobPayload args, CancellationToken cancellationToken);
}
