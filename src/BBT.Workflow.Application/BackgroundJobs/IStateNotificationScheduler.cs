using BBT.Workflow.Execution;

namespace BBT.Workflow.BackgroundJobs;

/// <summary>
/// Schedules the durable, one-shot job that dispatches a settled state's notification entries.
/// Enqueued from the transition pipeline once the instance state/status is finalized; the actual
/// notification dispatch (including rule evaluation) runs off the request thread so the client
/// response is never blocked.
/// </summary>
public interface IStateNotificationScheduler
{
    /// <summary>
    /// Enqueues a state-notify job for the settled state, carrying the request context
    /// (headers, route values, body) so rule and mapping scripts can run against a full
    /// <c>ScriptContext</c> in the durable job. The <c>InstanceJob</c> row joins the ambient
    /// transition unit of work (atomic with the transition commit); the Dapr enqueue is deferred
    /// to post-commit.
    /// </summary>
    Task ScheduleAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default);
}
