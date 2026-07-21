using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances;

/// <summary>
/// Client-workflow-manager interaction directives surfaced on the State (long-poll) function response.
/// A generic, extensible container: today it carries the long-poll directive. It is emitted whenever the
/// current state declares <c>interaction.longPoll</c> (subject to role grants), regardless of the
/// <c>terminate</c> value; future directives are added here as additional properties rather than at the
/// response root.
/// </summary>
public sealed class InstanceInteractionOutput
{
    /// <summary>
    /// When true, the client should terminate its active long-poll request, render the entered-state
    /// screen, and acknowledge via <see cref="Ack"/>. Reflects the state's <c>interaction.longPoll.terminate</c>
    /// value — it may be <c>false</c> when the state declares long-poll interaction without termination.
    /// </summary>
    public bool TerminateLongPoll { get; set; }

    /// <summary>
    /// Acknowledge fallback window in seconds (<c>interaction.longPoll.fallbackTimeoutSeconds</c>, default 60).
    /// Always present when the state declares <c>interaction.longPoll</c>. When <see cref="TerminateLongPoll"/>
    /// is true and the client does not acknowledge within this window, a scheduled fallback resumes the pipeline.
    /// </summary>
    public int FallbackTimeoutSeconds { get; set; }

    /// <summary>
    /// Acknowledge endpoint href. Present only when <see cref="TerminateLongPoll"/> is true. POSTing to it
    /// resumes the paused pipeline; if not called, a fallback schedule resumes it automatically.
    /// </summary>
    public AckHref? Ack { get; set; }
}
