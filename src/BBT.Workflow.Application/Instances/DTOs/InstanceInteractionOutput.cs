using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances;

/// <summary>
/// Client-workflow-manager interaction directives surfaced on the State (long-poll) function response.
/// A generic, extensible container: today it carries long-poll termination; future directives are
/// added here as additional properties rather than at the response root.
/// </summary>
public sealed class InstanceInteractionOutput
{
    /// <summary>
    /// When true, the client should terminate its active long-poll request, render the entered-state
    /// screen, and acknowledge via <see cref="Ack"/>. Set when the entered state declares
    /// <c>interaction.longPoll.terminate</c> and the caller's role is granted the signal.
    /// </summary>
    public bool TerminateLongPoll { get; set; }

    /// <summary>
    /// Acknowledge endpoint href. Present when <see cref="TerminateLongPoll"/> is true. POSTing to it
    /// resumes the paused pipeline; if not called, a fallback schedule resumes it automatically.
    /// </summary>
    public AckHref? Ack { get; set; }
}
