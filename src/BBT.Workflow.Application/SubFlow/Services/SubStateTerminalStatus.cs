using BBT.Workflow.Instances;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Recognises a <c>sub:state-changed</c> notification that reports the child's terminal
/// <c>Completed</c> status — the one a child publishes at the rest point of the hop that reached a
/// finish state (complete, cancel and exit all end in <c>Completed</c>).
/// <para>
/// Such a notification is superseded by the terminal event raised in the same commit
/// (<c>sub:completed</c> / <c>sub:canceled</c>). That event is relayed first — <c>HandleFinishStep</c>
/// defers it before <c>TransitionSettlement</c> publishes the state change, and the dispatcher relays
/// in order — and its settlement writes the child's final state onto the correlation, closes it and
/// re-derives the parent's projection from the parent's own state. Applied afterwards the state change
/// finds no open correlation and writes nothing; applied BEFORE it (the terminal relay failed, or the
/// backup won the race) it projects a finished child onto a parent that is about to resume, and on a
/// nested chain carries that projection to the grandparent, which the terminal settlement never
/// corrects upward. So both delivery paths drop it. <c>Faulted</c> never reaches this channel —
/// <c>TransitionSettlement</c> does not publish it — and a notification without a status (a publisher
/// that predates the field) is not recognised and keeps its old handling.
/// </para>
/// </summary>
internal static class SubStateTerminalStatus
{
    /// <summary>Reason written to logs and span tags when the notification is dropped.</summary>
    public const string SupersededReason = "terminal_superseded";

    public static bool IsSupersededByTerminalEvent(string? newStatus) =>
        InstanceStatus.TryFromCode(newStatus) is { } status && status.Equals(InstanceStatus.Completed);
}
