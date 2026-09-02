using System.Text.Json;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Request body of the internal-only SubFlow transition relay endpoint.
/// <para>
/// The relay cannot reuse the public transition endpoint because it must carry
/// <see cref="ChainReserved"/>, and that endpoint copies caller headers unfiltered while
/// serializing only the data element — a claim exposed there would be forgeable by any client and
/// would defeat the Busy-as-mutex guarantee. Carrying it in this body, on an endpoint protected by
/// network isolation (same posture as the related-data endpoints), keeps it server-internal.
/// </para>
/// </summary>
public record SubflowForwardInput
{
    /// <summary>Transition data attributes forwarded from the parent request.</summary>
    public JsonElement? Attributes { get; init; }

    /// <summary>Instance key forwarded from the parent request.</summary>
    public string? Key { get; init; }

    /// <summary>Instance tags forwarded from the parent request.</summary>
    public string[]? Tags { get; init; }

    /// <summary>Instance stage forwarded from the parent request.</summary>
    public string? Stage { get; init; }

    /// <summary>Whether the forwarded transition must execute synchronously.</summary>
    public bool Sync { get; init; }

    /// <summary>
    /// True when the originating accept reserved this SubFlow chain's Busy flag down to the leaf,
    /// so the target must admit the relay as an owner re-entry instead of rejecting the pre-set
    /// Busy with a 409.
    /// </summary>
    public bool ChainReserved { get; init; }

    /// <summary>Business correlation id of the originating execution chain.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Route values forwarded from the parent request.</summary>
    public Dictionary<string, string?> RouteValues { get; init; } = new();

    /// <summary>
    /// Trace lane anchor (W3C traceparent) of the forwarding parent. The relayed transition's span
    /// parents to it, so every hop of the subflow appears flat underneath the forwarding span rather
    /// than nested inside its predecessor. See <c>WorkflowTraceLane</c>.
    /// <para>
    /// Carried in this internal-only body rather than a header, exactly like <c>CorrelationId</c>:
    /// public endpoints must not be able to inject a lane, or a caller could graft its spans onto
    /// an unrelated trace. <c>FlatLaneActivity</c>'s trace-id check is the backstop.
    /// </para>
    /// </summary>
    public string? TraceRoot { get; init; }

    /// <summary>The enclosing lane's anchor, so the subflow's resume returns to the parent's lane.</summary>
    public string? ParentTraceRoot { get; init; }

    /// <summary>
    /// Start of the activation episode the forwarding parent is executing, so the subflow's
    /// time-to-Active is measured from the client's request rather than from this relay hop. A
    /// timestamp, not an anchor: it cannot graft spans onto another trace, so it needs no more
    /// protection than the lane fields already have. See <c>WorkflowTraceLane.Episode</c>.
    /// </summary>
    public DateTimeOffset? EpisodeStartedAt { get; init; }

    /// <summary>What opened the episode; one of <c>TelemetryConstants.ActivationTriggers</c>.</summary>
    public string? EpisodeTrigger { get; init; }

    /// <summary>The transition the episode was triggered with (the first hop's key).</summary>
    public string? EpisodeTransitionKey { get; init; }

    /// <summary>The trace root under which the activation episode began.</summary>
    public string? EpisodeTraceRoot { get; init; }
}
