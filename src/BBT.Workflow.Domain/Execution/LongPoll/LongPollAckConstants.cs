namespace BBT.Workflow.Execution.LongPoll;

/// <summary>
/// Constants for the declarative long-poll termination feature (long-poll acknowledge).
/// </summary>
public static class LongPollAckConstants
{
    /// <summary>
    /// Job-name suffix key for the acknowledge fallback schedule. The fallback job is named
    /// <c>lpack-{instanceId}-{JobKey}</c> so it can be cancelled via the existing
    /// transition-key-suffix cancellation path on acknowledge.
    /// </summary>
    public const string JobKey = "longpoll-ack";
}
