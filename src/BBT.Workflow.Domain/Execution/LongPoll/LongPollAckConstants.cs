namespace BBT.Workflow.Execution.LongPoll;

/// <summary>
/// Constants for the declarative long-poll termination feature (long-poll acknowledge).
/// </summary>
public static class LongPollAckConstants
{
    /// <summary>
    /// Well-known segment key for the acknowledge fallback schedule. The fallback job is built via
    /// <see cref="BBT.Workflow.Instances.JobName.ForLongPollAck"/> (type <c>la</c>) carrying this
    /// key as its segment, so it can be cancelled via the structured state-transition cancellation
    /// path on acknowledge (matched by job type + this key).
    /// </summary>
    public const string JobKey = "longpoll-ack";
}
