namespace BBT.Workflow.BackgroundJobs;

/// <summary>
/// Schedules the durable, one-shot job that dispatches a settled state's notification directive.
/// Enqueued from the transition pipeline once the instance state/status is finalized; the actual
/// notification dispatch runs off the request thread so the client response is never blocked.
/// </summary>
public interface IStateNotificationScheduler
{
    /// <summary>
    /// Enqueues a state-notify job for the settled state. The durable <c>InstanceJob</c> row joins
    /// the ambient transition unit of work (atomic with the transition commit); the Dapr enqueue is
    /// deferred to post-commit.
    /// </summary>
    Task ScheduleAsync(
        Guid instanceId,
        string domain,
        string flowName,
        string version,
        string stateKey,
        CancellationToken cancellationToken = default);
}
