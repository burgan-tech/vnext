using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances;

/// <summary>
/// Client-workflow-manager interaction directives surfaced on the State (long-poll) function response.
/// A generic, extensible container: today it carries the long-poll directive. It is emitted when the
/// current state declares <c>interaction.longPoll</c> and the caller passes its gate (rule or role
/// grants) — for a terminating state only while the acknowledge is outstanding, for a non-terminating
/// state whenever the instance is in it; future directives are added here as additional properties
/// rather than at the response root.
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
    /// Effective window in seconds (<c>interaction.longPoll.fallbackTimeoutSeconds</c>, default 60; a parent
    /// override applies). When <see cref="TerminateLongPoll"/> is true and the client does not acknowledge
    /// within this window, a scheduled fallback resumes the pipeline. When it is false nothing is armed
    /// server-side: the value tells the client how long to keep long polling, replacing its own default
    /// (e.g. a client defaulting to 60 s polls for 120 s when the state declares 120).
    /// </summary>
    public int FallbackTimeoutSeconds { get; set; }

    /// <summary>
    /// Acknowledge endpoint href. Present only when <see cref="TerminateLongPoll"/> is true. POSTing to it
    /// resumes the paused pipeline; if not called, a fallback schedule resumes it automatically.
    /// </summary>
    public AckHref? Ack { get; set; }
}
