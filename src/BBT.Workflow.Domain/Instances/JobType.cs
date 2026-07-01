namespace BBT.Workflow.Instances;

/// <summary>
/// Discriminates the kind of Dapr background job tracked by an <see cref="InstanceJob"/>.
/// The value is persisted on the instance-job row and is also encoded (as a short, stable
/// wire code) into the structured <see cref="JobName"/> so a job's purpose is resolvable
/// from its name alone.
/// </summary>
public enum JobType
{
    /// <summary>Unrecognized / legacy job name written before the structured-name rollout.</summary>
    Unknown = 0,

    /// <summary>Async transition continuation (handler <c>flow.transition</c>).</summary>
    AsyncTransition = 1,

    /// <summary>Timer-based scheduled transition (handler <c>flow.transition.schedule</c>).</summary>
    ScheduledTransition = 2,

    /// <summary>Workflow timeout (handler <c>flow.timeout</c>).</summary>
    Timeout = 3,

    /// <summary>Long-poll acknowledge fallback (handler <c>longpoll.ack.timeout</c>).</summary>
    LongPollAck = 4,

    /// <summary>State-level notification dispatch (handler <c>state.notify</c>).</summary>
    StateNotify = 5
}
