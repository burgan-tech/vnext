using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Shared;

/// <summary>
/// Base class for models that contain href properties
/// </summary>
public class HrefBase
{
    /// <summary>
    /// The href URL for the resource
    /// </summary>
    public string Href { get; set; } 
}

/// <summary>
/// Transition item with href link
/// </summary>
public sealed class TransitionItem : HrefBase
{
    /// <summary>
    /// Transition name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Transition category for client behavior, e.g. stateTransition, sharedTransition, cancel, exit,
    /// updateData, timeout, scheduled.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The UTC instant the runtime is armed to execute this transition at. Present only on
    /// <c>kind: "scheduled"</c> entries (read from the persisted job state); null — and omitted from
    /// the JSON — for every caller-triggerable kind. Always <see cref="DateTimeKind.Utc"/>, so it
    /// serializes with the <c>Z</c> designator.
    /// </summary>
    public DateTime? ExecuteAtUtc { get; set; }

    /// <summary>
    /// View href for this transition. When HasView is true, the view endpoint returns meaningful content when called with this transition key.
    /// </summary>
    public ViewHref? View { get; set; }

    /// <summary>
    /// Schema href for this transition. When HasSchema is true, the schema endpoint returns meaningful content for this transition key.
    /// </summary>
    public SchemaHref? Schema { get; set; }

    /// <summary>
    /// Optional key-value metadata for client-side filtering and UI context.
    /// Use namespaced keys to avoid collisions (e.g., <c>ui/visible-in</c>, <c>ui/priority</c>).
    /// </summary>
    public Dictionary<string, string>? Annotations { get; set; }
}

/// <summary>
/// Data href link
/// </summary>
public sealed class DataHref : HrefBase
{
}

/// <summary>
/// Schema href link with has-schema flag. When true, the schema endpoint returns meaningful content for the transition.
/// </summary>
public sealed class SchemaHref : HrefBase
{
    /// <summary>
    /// Whether this transition has a schema reference. When true, the schema endpoint returns meaningful content.
    /// </summary>
    public bool HasSchema { get; set; }
}

/// <summary>
/// Master schema href link. Points to the master function endpoint, which returns the flow-level
/// master schema the instance is bound to (forwarding to the active subflow when present).
/// </summary>
public sealed class MasterHref : HrefBase
{
}

/// <summary>
/// Acknowledge href link (e.g. the long-poll termination acknowledge endpoint).
/// </summary>
public sealed class AckHref : HrefBase
{
}

/// <summary>
/// Incident block of the state function body: whether the polled instance (or the deepest active
/// subflow it delegates to) currently carries an unresolved error-boundary incident, a safe summary of
/// that incident, and a link to the paged incident history. Always present so clients can branch on
/// <see cref="HasActiveIncident"/> deterministically. Visible to every caller who passes the
/// state function's <c>queryRoles</c> gate; never carries a stack trace.
/// </summary>
public sealed class IncidentHref
{
    /// <summary>True while an unresolved incident is recorded on the displayed instance.</summary>
    public bool HasActiveIncident { get; set; }

    /// <summary>The newest unresolved incident, or null when <see cref="HasActiveIncident"/> is false.</summary>
    public IncidentSummary? Active { get; set; }

    /// <summary>Link to the paged incident history endpoint of the polled instance.</summary>
    public string HistoryHref { get; set; } = string.Empty;
}

/// <summary>
/// Client-safe projection of one incident: what failed, where, and how to correlate it — no stack trace.
/// </summary>
public sealed class IncidentSummary
{
    /// <summary>Unique incident identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Normalized error code (e.g. "Task:Http:503").</summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>Human-readable error message (truncated by the domain).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>State where the error occurred.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Transition that was executing when the error occurred.</summary>
    public string Transition { get; set; } = string.Empty;

    /// <summary>Task key that failed; null for pipeline-level errors.</summary>
    public string? Task { get; set; }

    /// <summary>Error layer: "Transport", "Task", "Pipeline", "SubFlow", "Job", "PostCommit".</summary>
    public string? ErrorLayer { get; set; }

    /// <summary>HTTP status code when applicable.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Error boundary action taken, e.g. "Abort", "Retry", "Notify".</summary>
    public string? BoundaryAction { get; set; }

    /// <summary>OpenTelemetry trace identifier for correlation with distributed traces.</summary>
    public string? TraceId { get; set; }

    /// <summary>When the incident occurred (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// View href link with load data flag
/// </summary>
public sealed class ViewHref : HrefBase
{
    /// <summary>
    /// Whether the current state has a view definition (state view or wizard single-transition view). When true, the view endpoint returns meaningful content.
    /// </summary>
    public bool HasView { get; set; }

    /// <summary>
    /// Whether to load data
    /// </summary>
    public bool LoadData { get; set; } = true;
}

/// <summary>
/// Executable href for a function, carrying the HTTP verbs the function accepts. An empty verb list
/// means the function declares no restriction and answers any verb.
/// </summary>
public sealed class FunctionHref : HrefBase
{
    /// <summary>
    /// HTTP verbs the function accepts, upper-cased. Empty means unrestricted.
    /// </summary>
    public List<string> Verbs { get; set; } = [];
}

/// <summary>
/// Pointer to the workflow's function catalog. Emitted in the state response so a client learns
/// whether the flow ships any functions at all, and where to enumerate them, without the state
/// response having to carry — or resolve — the list itself.
/// </summary>
public sealed class FunctionsHref : HrefBase
{
    /// <summary>
    /// Whether the workflow declares any functions. When false the catalog endpoint returns an empty
    /// list, so a client can skip the call entirely.
    /// </summary>
    public bool HasFunctions { get; set; }
}

/// <summary>
/// A function declared on the workflow, linked to its discovery (<c>info</c>) endpoint. Returned by
/// the <c>catalog</c> function.
/// </summary>
public sealed class WorkflowFunctionHref : HrefBase
{
    /// <summary>The function key.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The function version the workflow pins, or empty when it tracks the latest.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Scope code the function declares: <c>D</c> (Domain), <c>F</c> (Flow) or <c>I</c> (Instance).
    /// <see cref="HrefBase.Href"/> is built to match — a Domain-scoped function links to the
    /// domain route, Flow and Instance scopes to the instance route, since the domain route rejects them.
    /// </summary>
    public string Scope { get; set; } = string.Empty;
}

/// <summary>
/// The workflow's function catalog: every function the flow declares that the caller may actually
/// invoke, each linked to its <c>info</c> endpoint.
/// </summary>
public sealed class FunctionCatalogOutput
{
    /// <summary>
    /// Functions in declaration order. Empty when the workflow declares none, or when the caller's
    /// roles exclude all of them.
    /// </summary>
    public List<WorkflowFunctionHref> Functions { get; set; } = [];
}

/// <summary>
/// Child correlation with href link and additional properties. Used for both the active-only
/// <c>activeCorrelations</c> list and the full <c>correlations</c> list (active + completed);
/// <see cref="IsCompleted"/> distinguishes the two within the full list.
/// </summary>
public sealed class ActiveCorrelationHref : HrefBase
{
    /// <summary>
    /// Correlation ID
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Parent state
    /// </summary>
    public string ParentState { get; set; } = string.Empty;

    /// <summary>
    /// SubFlow instance ID
    /// </summary>
    public Guid SubFlowInstanceId { get; set; }

    /// <summary>
    /// SubFlow type
    /// </summary>
    public SubFlowType SubFlowType { get; set; }

    /// <summary>
    /// SubFlow domain
    /// </summary>
    public string SubFlowDomain { get; set; } = string.Empty;

    /// <summary>
    /// SubFlow name
    /// </summary>
    public string SubFlowName { get; set; } = string.Empty;

    /// <summary>
    /// SubFlow version
    /// </summary>
    public string? SubFlowVersion { get; set; }

    /// <summary>
    /// Whether the correlation is completed
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// When the correlation was completed; null while it is still active.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// How the sub item terminated (Completed / Faulted / Canceled). Null while the correlation is
    /// active, and also null for legacy rows completed before the outcome was recorded.
    /// </summary>
    public SubItemTerminalOutcome? TerminalOutcome { get; set; }

    /// <summary>
    /// When the correlation was created — the stable ordering key for the full correlation list.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Reserved for the sub item's instance status. Not populated today — use the sub item's own
    /// state endpoint (see <see cref="HrefBase.Href"/>) when the live status is required.
    /// </summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>
    /// Last known state of the sub item, tracked on the parent correlation so the parent does not
    /// need a cross-domain query. Null until the sub item reports its first state change.
    /// </summary>
    public string? CurrentState { get; set; }

    /// <summary>
    /// When <see cref="CurrentState"/> was last updated.
    /// </summary>
    public DateTime? StateChangedAt { get; set; }
}
