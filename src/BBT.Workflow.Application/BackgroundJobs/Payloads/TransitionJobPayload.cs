using System.Text.Json;
using BBT.Workflow.Shared;

namespace BBT.Workflow.BackgroundJobs.Payloads;

/// <summary>
/// Payload for asynchronous transition background jobs.
/// Contains all necessary information to execute a workflow transition in the background.
/// </summary>
public sealed class TransitionJobPayload : ITraceableJobPayload
{
    public string JobName { get; set; }
    
    /// <summary>
    /// Gets or sets the instance ID for the transition.
    /// </summary>
    public Guid InstanceId { get; set; }
    
    /// <summary>
    /// Gets or sets the transition key to execute.
    /// </summary>
    public string TransitionKey { get; set; } = default!;
    
    /// <summary>
    /// Gets or sets the domain name for the workflow.
    /// </summary>
    public string Domain { get; set; } = default!;
    
    /// <summary>
    /// Gets or sets the workflow name.
    /// </summary>
    public string Workflow { get; set; } = default!;

    /// <summary>
    /// Gets the workflow name for tracing purposes.
    /// Maps to <see cref="Workflow"/> property for ITraceableJobPayload compatibility.
    /// </summary>
    string ITraceableJobPayload.FlowName => Workflow;

    /// <summary>
    /// Gets or sets the workflow version.
    /// </summary>
    public string Version { get; set; } = default!;
    
    /// <summary>
    /// Gets or sets the transition data as JSON.
    /// </summary>
    public JsonElement? Data { get; set; }

    /// <summary>
    /// Gets or sets the original, unmodified request body (as a literal string) captured at accept time.
    /// Carried so background mappings can verify signatures (JWS / mTLS) over the exact payload bytes.
    /// A string round-trips byte-for-byte through the camelCased job payload serialization, unlike <see cref="Data"/>.
    /// </summary>
    public string? RawBody { get; set; }

    /// <summary>
    /// Gets or sets the instance key (Optional).
    /// </summary>
    public string? InstanceKey { get; set; }

    /// <summary>
    /// Gets or sets the instance tags (Optional).
    /// </summary>
    public string[]? Tags { get; set; }
    
    /// <summary>
    /// Gets or sets the instance stage (Optional).
    /// </summary>
    public string? Stage { get; set; }

    /// <summary>
    /// Gets or sets the request headers.
    /// </summary>
    public Dictionary<string, string?> Headers { get; set; } = new();
    
    /// <summary>
    /// Gets or sets the route values.
    /// </summary>
    public Dictionary<string, string?> RouteValues { get; set; } = new();
    
    /// <summary>
    /// Gets or sets the execution context for the transition.
    /// </summary>
    public ExecutionActor ExecutionActor { get; set; } = ExecutionActor.User;

    /// <summary>
    /// Preserves the original caller's sync/async intent.
    /// When false, subflows started from this transition will also run asynchronously.
    /// </summary>
    public bool CallerSync { get; set; }

    /// <summary>
    /// Gets or sets the W3C Trace Context traceparent header for distributed tracing correlation.
    /// Format: {version}-{trace-id}-{parent-id}-{trace-flags}
    /// </summary>
    public string? TraceParent { get; set; }

    /// <summary>
    /// Gets or sets the W3C Trace Context tracestate header for vendor-specific trace data.
    /// </summary>
    public string? TraceState { get; set; }

    /// <summary>
    /// Trace lane anchor this hop's span parents to (see <c>WorkflowTraceLane</c>). Distinct from
    /// <see cref="TraceParent"/>: the anchor is the PARENT, the traceparent is the PREDECESSOR that
    /// gets linked. That distinction is what turns a nested chain into a flat lane.
    /// </summary>
    public string? TraceRoot { get; set; }

    /// <summary>
    /// The enclosing lane's anchor, propagated so a subflow's eventual resume returns to the parent
    /// instance's lane. Null outside a subflow.
    /// </summary>
    public string? ParentTraceRoot { get; set; }

    /// <summary>
    /// Chain depth of this hop, carried so the lane span can be tagged with it without re-reading
    /// the instance. Resets to 0 at resume/timeout/retry boundaries — use <see cref="LaneSeq"/> to
    /// order a lane.
    /// </summary>
    public int ChainDepth { get; set; }

    /// <summary>
    /// Monotonic ordinal of this hop within its lane. Computed ONCE at the enqueue site and copied
    /// into both the direct payload and the outbox event, because the enqueue gateway may fall back
    /// from one to the other and incrementing in two places would produce duplicate ordinals.
    /// </summary>
    public int LaneSeq { get; set; }

    /// <summary>
    /// Start of the activation episode this hop continues — the instant the originating request
    /// (or timer, event, resume) was accepted. Null from a build that predates episodes; the
    /// consuming hop then reports a partial span. See <c>WorkflowTraceLane.Episode</c>.
    /// </summary>
    public DateTimeOffset? EpisodeStartedAt { get; set; }

    /// <summary>What opened the episode; one of <c>TelemetryConstants.ActivationTriggers</c>.</summary>
    public string? EpisodeTrigger { get; set; }

    /// <summary>The transition the episode was triggered with (the first hop's key).</summary>
    public string? EpisodeTransitionKey { get; set; }

    /// <summary>
    /// Business correlation id of the originating execution chain. Restored into the rebuilt
    /// <c>TransitionInput</c> by the job handler so the async hop keeps the SAME correlation.id
    /// instead of minting a new one per job.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// True when the accept that created this job reserved the instance's whole active SubFlow
    /// chain down to the leaf (see <c>AsyncTransitionStrategy</c>). The relay step reads it to
    /// stamp the claim onto the forward, so the leaf admits the request as an owner re-entry
    /// instead of rejecting the Busy it was pre-set to. False for every other accept — a forward
    /// must never claim a reserve that was not taken, or it would barge past a leaf that is Busy
    /// for its own reasons.
    /// </summary>
    public bool SubflowChainReserved { get; set; }
}
