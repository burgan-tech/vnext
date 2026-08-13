using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances;

/// <summary>
/// Output for retrieving instance state with combined information
/// </summary>
public sealed class GetInstanceStateOutput
{
    /// <summary>
    /// Data href link with optional extensions
    /// </summary>
    public DataHref Data { get; set; } = new();

    /// <summary>
    /// View href link
    /// </summary>
    public ViewHref View { get; set; } = new();

    /// <summary>
    /// Master schema href link. Points to the master function endpoint that returns the flow-level
    /// master schema the instance is bound to (forwarding to the active subflow when present).
    /// </summary>
    public MasterHref Master { get; set; } = new();

    /// <summary>
    /// Current state of the instance
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Current state's type in camelCase.
    /// </summary>
    public string StateType { get; set; } = string.Empty;

    /// <summary>
    /// Instance status
    /// </summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>
    /// Active correlations with href links
    /// </summary>
    public List<ActiveCorrelationHref> ActiveCorrelations { get; set; } = [];

    /// <summary>
    /// Full child correlation set — active and completed — ordered by creation time ascending.
    /// Clients that need the sub item history (which subflows ran and how they terminated) read this
    /// list and filter on <see cref="ActiveCorrelationHref.IsCompleted"/>;
    /// <see cref="ActiveCorrelations"/> keeps its active-only semantics.
    /// Read from a dedicated query, so under concurrent completion its active subset can be a moment
    /// fresher than <see cref="ActiveCorrelations"/>.
    /// </summary>
    public List<ActiveCorrelationHref> Correlations { get; set; } = [];

    /// <summary>
    /// Available transition items with href links
    /// </summary>
    public List<TransitionItem> Transitions { get; set; } = [];

    /// <summary>
    /// Transitions the runtime has armed to fire automatically for this instance, ordered by
    /// execution time ascending. Read from the persisted job state, so clients can render
    /// countdowns and upcoming-action information without any scheduler access. Always describes
    /// the polled instance itself — during an active-subflow window it is not merged with the
    /// subflow's own list. Changes to this set deliberately do NOT participate in the fingerprint
    /// ETag (team decision, issue #864) — a same-state re-arm can leave this list stale behind a
    /// 304; accepted as a known gap, see <c>InstanceStateFingerprint</c>.
    /// </summary>
    public List<ScheduledTransitionItem> ScheduledTransitions { get; set; } = [];

    /// <summary>
    /// Pointer to the workflow's function catalog: whether the flow declares any functions, and where
    /// to enumerate them.
    /// </summary>
    /// <remarks>
    /// Only the flag and the href live here, never the list. Resolving the list means reading every
    /// declared function's component to learn its scope and evaluating its roles — far too much for a
    /// response served on every long-poll. Deliberately outside the ETag material: <c>hasFunctions</c>
    /// is a property of the flow version, which the fingerprint already covers, so it cannot change
    /// while an instance is parked. See <c>StateFunctionCache.ResponseShapeVersion</c>.
    /// </remarks>
    public FunctionsHref Functions { get; set; } = new();

    /// <summary>
    /// Client-workflow-manager interaction directives for the current state. Present only when the
    /// state declares an applicable directive for the caller (today: long-poll termination). A generic
    /// container so future interaction directives are grouped under the same key.
    /// </summary>
    public InstanceInteractionOutput? Interaction { get; set; }

    /// <summary>
    /// Representation ETag (RFC 7232 quoted) for cache validation.
    /// </summary>
    public string? ETag
    {
        get
        {
            if (string.IsNullOrEmpty(_etag))
                return null;
            var unquoted = _etag.Replace("\"", "");
            return $"\"{unquoted}\"";
        }
        set => _etag = value;
    }
    private string? _etag = string.Empty;

    /// <summary>
    /// Entity (DB row) version for concurrency, RFC 7232 quoted. Exposed as X-Entity-ETag response header.
    /// </summary>
    public string? EntityEtag
    {
        get
        {
            if (string.IsNullOrEmpty(_entityEtag))
                return null;
            var unquoted = _entityEtag.Replace("\"", "");
            return $"\"{unquoted}\"";
        }
        set => _entityEtag = value;
    }
    private string? _entityEtag = string.Empty;
}
