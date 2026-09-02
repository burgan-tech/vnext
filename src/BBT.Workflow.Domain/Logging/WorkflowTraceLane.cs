using System.Diagnostics;

namespace BBT.Workflow.Logging;

/// <summary>
/// Carries the ambient <em>trace lane anchor</em> — the span that every top-level operation of the
/// current business request must be parented to.
/// <para>
/// Without an anchor, each auto-chained transition hop parents to the previous hop's span, so trace
/// nesting depth equals chain depth and a long chain (or any subflow) becomes unreadable. The anchor
/// makes those hops <em>siblings</em> instead: hop N+1 is parented to the anchor, with hop N attached
/// as an <see cref="ActivityLink"/> so causality is still discoverable.
/// </para>
/// <para>
/// The model is <b>one lane per instance</b>. <see cref="Current"/> is the anchor for the instance
/// being executed; <see cref="ParentLane"/> is the lane to return to. A subflow handoff calls
/// <see cref="EnterChildLane"/>, which makes the handing-off span (e.g.
/// <c>PostCommit.ForwardToSubflowJob</c>) the child instance's anchor — so the subflow's own hops are
/// flat <em>underneath</em> it, and total depth grows with subflow nesting rather than chain length.
/// A subflow resume is a parent-instance operation and anchors to <see cref="ParentLane"/>.
/// </para>
/// <para>
/// The anchor is a W3C <c>traceparent</c> <b>string</b>, not an <see cref="ActivityContext"/>: it has
/// to round-trip through JSON (background job payloads, outbox events, internal relay bodies) and
/// keeping it a string leaves this type free of behavioural OpenTelemetry coupling.
/// </para>
/// <para>
/// W3C baggage cannot serve as the carrier. Every helper in this codebase starts spans with an
/// explicit <see cref="ActivityContext"/>, which leaves <see cref="Activity.Parent"/> null; since
/// <see cref="Activity.Baggage"/> walks the parent chain, baggage is invisible to those spans
/// already (pinned by <c>ActivityParentContextSemanticsTests</c>). Baggage also cannot survive the
/// Dapr scheduler callback — a fresh HTTP request the sidecar originates — nor the outbox table.
/// </para>
/// </summary>
public static class WorkflowTraceLane
{
    // A single AsyncLocal holding an immutable pair. Two independent AsyncLocals could be restored
    // separately and leave the lane and its parent describing different requests.
    private static readonly AsyncLocal<LaneScopeState?> State = new();

    /// <summary>
    /// The current instance's lane anchor as a W3C traceparent, or null when no lane is established
    /// (in which case callers must fall back to today's ambient-parent behaviour).
    /// </summary>
    public static string? Current => State.Value?.Anchor;

    /// <summary>
    /// The enclosing lane's anchor — the lane a subflow resume returns to. Null at the top level.
    /// </summary>
    public static string? ParentLane => State.Value?.ParentAnchor;

    /// <summary>
    /// Ordinal of the hop currently executing within its lane. Needed because <c>ChainDepth</c>
    /// resets to 0 at subflow resume, long-poll resume, timeout and retry boundaries, so it cannot
    /// order a lane on its own. Callers stamp <see cref="NextSeq"/> on the work they enqueue.
    /// </summary>
    public static int Seq => State.Value?.Seq ?? 0;

    /// <summary>
    /// The <em>activation episode</em> the current work belongs to: when the trigger that set this
    /// instance in motion was accepted, and what that trigger was. Read at the rest point (the
    /// Busy→Active flip, a terminal status, a rest-in-Busy) to emit the <c>Instance.Activation</c>
    /// span whose duration is exactly what a client waited. Null when no entry point seeded one.
    /// <para>
    /// Rides the lane, not the execution context, for the same reason the anchor does: an
    /// <see cref="System.Threading.AsyncLocal{T}"/> flows through inline auto-chain hops, the
    /// post-commit barrier and the terminal relay on its own, and only the async boundaries that
    /// already carry the anchor (job payloads, outbox events, internal relay bodies) need a field.
    /// </para>
    /// </summary>
    public static ActivationEpisode? Episode => State.Value?.Episode;

    /// <summary>
    /// The ordinal to stamp on the next hop. Compute it ONCE per enqueue and copy the same value
    /// into both the direct payload and the outbox event — the enqueue gateway may fall back from
    /// one to the other, and incrementing in two places would produce duplicate ordinals.
    /// </summary>
    public static int NextSeq() => Seq + 1;

    /// <summary>
    /// Establishes an explicit lane. A null <paramref name="anchor"/> <b>preserves</b> the current
    /// lane rather than clearing it, so a legacy payload that carries no anchor still flattens into
    /// the live HTTP request's lane instead of starting a nested one.
    /// </summary>
    /// <param name="anchor">The lane anchor (W3C traceparent), or null to keep the current one.</param>
    /// <param name="parentAnchor">The enclosing lane's anchor, or null to keep the current one.</param>
    /// <param name="seq">The hop ordinal, or null to keep the current one.</param>
    /// <param name="episode">The activation episode, or null to keep the current one.</param>
    public static IDisposable Use(
        string? anchor,
        string? parentAnchor = null,
        int? seq = null,
        ActivationEpisode? episode = null)
    {
        var previous = State.Value;
        State.Value = new LaneScopeState(
            anchor ?? previous?.Anchor,
            parentAnchor ?? previous?.ParentAnchor,
            seq ?? previous?.Seq ?? 0,
            episode ?? previous?.Episode);
        return new LaneScope(previous);
    }

    /// <summary>
    /// Replaces the lane with exactly the supplied values, <b>clearing</b> it when
    /// <paramref name="anchor"/> is null — unlike <see cref="Use"/>, which preserves.
    /// <para>
    /// This is the job-handler entry policy. A Dapr scheduler callback is itself an HTTP request, so
    /// the request middleware has already anchored the lane on the <em>callback</em> span; that span
    /// belongs to the transport, not to the originating business request, and inheriting it would
    /// make every legacy-payload hop look like a cross-trace anchor mismatch. Resetting means a
    /// payload without an anchor degrades cleanly to the pre-lane shape instead.
    /// </para>
    /// </summary>
    /// <param name="anchor">The lane anchor (W3C traceparent); null clears the lane.</param>
    /// <param name="parentAnchor">The enclosing lane's anchor; null clears it.</param>
    /// <param name="seq">The hop ordinal.</param>
    /// <param name="episode">The activation episode carried by the payload; null clears it, so a
    /// payload from a build that predates episodes does not inherit the callback request's.</param>
    public static IDisposable Reset(
        string? anchor,
        string? parentAnchor = null,
        int seq = 0,
        ActivationEpisode? episode = null)
    {
        var previous = State.Value;
        State.Value = new LaneScopeState(anchor, parentAnchor, seq, episode);
        return new LaneScope(previous);
    }

    /// <summary>
    /// Anchors the lane on <see cref="Activity.Current"/>, keeping the enclosing lane unchanged, and
    /// opens an activation episode that starts when that span started. Used at request entry (the
    /// ASP.NET server span — so the episode begins the instant the request arrived, before any
    /// endpoint code ran) and as the legacy fallback inside a job handler whose payload carries no
    /// anchor.
    /// </summary>
    /// <param name="episodeTrigger">What opened the episode; the entry point refines it later via
    /// <see cref="UseEpisode"/> once it knows which transition it is running.</param>
    public static IDisposable UseCurrentActivity(string episodeTrigger = TelemetryConstants.ActivationTriggers.Http)
        => Use(Activity.Current?.Id, episode: ActivationEpisode.StartingAt(Activity.Current, episodeTrigger));

    /// <summary>
    /// Classifies the current episode without moving its start. The request middleware seeds every
    /// episode as <see cref="TelemetryConstants.ActivationTriggers.Http"/> before the endpoint knows
    /// what it is; the first entry point to call this names the trigger, and later ones keep it —
    /// an event delivery classifies itself as <c>event</c> before it re-enters the generic
    /// transition entry point, which must not overwrite that with <c>manual</c>. The transition key
    /// is replaced whenever one is supplied (a subflow's own start names the child's span after the
    /// child's start transition). Seeds a fresh episode starting now when none is ambient.
    /// </summary>
    public static IDisposable UseEpisode(string trigger, string? transitionKey)
    {
        var previous = State.Value;
        var current = previous?.Episode;
        var episode = current is null
            ? new ActivationEpisode(DateTimeOffset.UtcNow, trigger, transitionKey, Partial: false)
            : current with
            {
                Trigger = current.Trigger == TelemetryConstants.ActivationTriggers.Http ? trigger : current.Trigger,
                TransitionKey = transitionKey ?? current.TransitionKey
            };
        State.Value = new LaneScopeState(previous?.Anchor, previous?.ParentAnchor, previous?.Seq ?? 0, episode);
        return new LaneScope(previous);
    }

    /// <summary>
    /// Opens a <b>child</b> lane for a subflow handoff: <see cref="Activity.Current"/> (the handing-off
    /// span) becomes the child instance's anchor, and the lane being left becomes
    /// <see cref="ParentLane"/> so the eventual resume can return to it.
    /// <para>
    /// The activation episode is <b>inherited</b> by default: the client that started the parent is
    /// polling the parent, which reports the leaf subflow's status, so the child's time-to-Active is
    /// measured from the parent's request. Pass <paramref name="restartTrigger"/> when the handoff
    /// is NOT something the originating client waits on — a trigger-family task starting an
    /// unrelated instance — and the child's episode starts at the handing-off span instead.
    /// </para>
    /// </summary>
    public static IDisposable EnterChildLane(string? restartTrigger = null)
    {
        var previous = State.Value;
        var episode = restartTrigger is null
            ? previous?.Episode
            : ActivationEpisode.StartingAt(Activity.Current, restartTrigger);
        // The child lane starts its own numbering: its hops belong to a different instance.
        State.Value = new LaneScopeState(Activity.Current?.Id, previous?.Anchor, Seq: 0, episode);
        return new LaneScope(previous);
    }

    private sealed record LaneScopeState(string? Anchor, string? ParentAnchor, int Seq, ActivationEpisode? Episode);

    private sealed class LaneScope(LaneScopeState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            State.Value = previous;
        }
    }
}
