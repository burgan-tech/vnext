using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances;

/// <summary>
/// Output for single instance retrieval with extensions
/// </summary>
public sealed class GetInstanceOutput
{
    public Guid? Id { get; set; }
    public string? Key { get; set; } = string.Empty;
    public string? Flow { get; set; } = string.Empty;
    public string? Domain { get; set; } = string.Empty;
    public string? FlowVersion { get; set; } = string.Empty;
    /// <summary>
    /// ETag value returned with quotes per RFC 7232.
    /// </summary>
    public string? ETag
    {
        get
        {
            if (string.IsNullOrEmpty(_etag))
                return null;
            // Strip any existing quotes and wrap with quotes per RFC 7232
            var unquoted = _etag.Replace("\"", "");
            return $"\"{unquoted}\"";
        }
        set => _etag = value;
    }
    private string? _etag = string.Empty;

    /// <summary>
    /// Entity (DB row) version for concurrency and write operations, returned with quotes per RFC 7232. Exposed as X-Entity-ETag response header.
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

    public List<string>? Tags { get; set; } = [];
    /// <summary>
    /// Instance metadata (state, audit, duration). Excludes fields already at root (id, key, flow, domain, flowVersion, eTag, tags).
    /// </summary>
    public InstanceMetadataDto? Metadata { get; set; }
    public JsonElement? Attributes { get; set; }
    public Dictionary<string, object>? Extensions { get; set; }
}

/// <summary>
/// Instance metadata returned in GetInstanceOutput. Contains state, audit and duration fields only; root identity fields are not duplicated.
/// </summary>
public sealed class InstanceMetadataDto
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceMetadataDto"/> class.
    /// </summary>
    public InstanceMetadataDto()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceMetadataDto"/> class from an <see cref="Instance"/> entity.
    /// </summary>
    /// <param name="instance">The instance entity.</param>
    public InstanceMetadataDto(Instance instance)
    {
        CurrentState = instance.CurrentState;
        EffectiveState = instance.EffectiveState;
        Status = instance.Status;
        EffectiveStatus = instance.GetEffectiveStatus;
        Type = instance.Type;
        EffectiveStateType = instance.EffectiveStateType;
        EffectiveStateSubType = instance.EffectiveStateSubType;
        CurrentStateType = instance.CurrentStateType;
        CurrentStateSubType = instance.CurrentStateSubType;
        Stage = instance.Stage;
        CompletedAt = instance.CompletedAt;
        Duration = instance.Duration?.TotalSeconds;
        CreatedAt = instance.CreatedAt;
        ModifiedAt = instance.ModifiedAt;
        CreatedBy = instance.CreatedBy;
        CreatedByBehalfOf = instance.CreatedByBehalfOf;
        ModifiedBy = instance.ModifiedBy;
        ModifiedByBehalfOf = instance.ModifiedByBehalfOf;

        // The incident block is NOT built here: it is links now, and a link needs the domain and
        // workflow this constructor does not have. The query service sets it for every response.
    }
    
    /// <summary>Current state key (engine internal state).</summary>
    public string? CurrentState { get; set; }

    /// <summary>Effective state exposed to callers (e.g. SubFlow state when active).</summary>
    public string? EffectiveState { get; set; }

    /// <summary>Instance status (Active, Completed, Faulted, etc.).</summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>
    /// The status a client observes for this instance: the deepest active SubFlow's status when one
    /// is running, otherwise this instance's own <see cref="Status"/>. The status counterpart of
    /// <see cref="EffectiveState"/>, and the same value the state function reports as its
    /// <c>status</c> — so a client can branch on either surface and get one answer.
    /// </summary>
    /// <remarks>
    /// Differs from <see cref="Status"/> exactly while a SubFlow is running: the parent is <c>Busy</c>
    /// for the child's whole lifetime by design, so <see cref="Status"/> says "something is in
    /// flight" and this says what that something is currently doing (for example <c>Active</c> while
    /// the child waits on a human task). Null only on a cross-domain response from a runtime that
    /// predates the field.
    /// </remarks>
    public InstanceStatus? EffectiveStatus { get; set; }

    /// <summary>
    /// How this instance was STARTED: <c>R</c> root, <c>S</c> SubFlow child, <c>P</c> SubProcess
    /// child. Immutable — stamped at creation and never updated, so it stays answerable long after
    /// the instance has finished.
    /// </summary>
    /// <remarks>
    /// This records the start origin, not a live relationship: it says a child was created by a
    /// parent's SubFlow state or SubProcess task, not that the link is still open. Null only on a
    /// cross-domain response from a runtime that predates the field.
    /// </remarks>
    public InstanceType? Type { get; set; }

    /// <summary>Type of the effective state (Initial, Intermediate, Finish, SubFlow, Wizard).</summary>
    public StateType? EffectiveStateType { get; set; }

    /// <summary>Subtype of the effective state (None, Success, Error, Terminated, Suspended, Busy, Human).</summary>
    public StateSubType? EffectiveStateSubType { get; set; }

    /// <summary>Type of the current state (Initial, Intermediate, Finish, SubFlow, Wizard).</summary>
    public StateType? CurrentStateType { get; set; }

    /// <summary>Subtype of the current state (None, Success, Error, Terminated, Suspended, Busy, Human).</summary>
    public StateSubType? CurrentStateSubType { get; set; }

    /// <summary>User-defined stage label for the instance.</summary>
    public string? Stage { get; set; }

    /// <summary>When the instance completed. Null if not completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Total duration in seconds from creation to completion. Null if not completed.</summary>
    public double? Duration { get; set; }

    /// <summary>When the instance was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the instance was last modified.</summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>Creator user identifier.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Creator behalf-of user identifier.</summary>
    public string? CreatedByBehalfOf { get; set; }

    /// <summary>Modifier user identifier.</summary>
    public string? ModifiedBy { get; set; }

    /// <summary>Modifier behalf-of user identifier.</summary>
    public string? ModifiedByBehalfOf { get; set; }

    /// <summary>
    /// Error boundary incident block: the flag plus links. Always present.
    /// </summary>
    public IncidentInfoDto Incident { get; set; } = new();
}

/// <summary>
/// Incident block exposed in instance metadata: whether an unresolved error-boundary incident exists,
/// plus links to fetch it and to page the history. Mirrors the state function's block exactly, so a
/// client learns one shape.
/// </summary>
/// <remarks>
/// <b>Links, not content.</b> This block used to embed the active incident, the newest five and a
/// total count, which cost two reads on every instance GET and one batch read per page of the list
/// view. Nothing is embedded now: the flag answers "is something wrong?", and a client that wants the
/// detail follows <see cref="Active"/>.
/// </remarks>
public sealed class IncidentInfoDto
{
    /// <summary>Whether the instance currently has an unresolved incident.</summary>
    public bool HasActiveIncident { get; set; }

    /// <summary>
    /// Link to the active incident, or null — and omitted from the JSON — when
    /// <see cref="HasActiveIncident"/> is false.
    /// </summary>
    public ActiveIncidentHref? Active { get; set; }

    /// <summary>Link to the paged incident history of this instance. Always present.</summary>
    public IncidentHistoryHref History { get; set; } = new();

    /// <summary>
    /// Builds the block from the instance's denormalized flag and the two supplied links. Reads
    /// nothing: the flag is a column on the instance, so this costs no query.
    /// </summary>
    public static IncidentInfoDto FromInstance(Instance instance, string activeHref, string historyHref) => new()
    {
        HasActiveIncident = instance.HasActiveIncident,
        Active = instance.HasActiveIncident ? new ActiveIncidentHref { Href = activeHref } : null,
        History = new IncidentHistoryHref { Href = historyHref }
    };
}

/// <summary>
/// Detail of a single error boundary incident.
/// </summary>
public sealed class IncidentDetailDto
{
    /// <summary>Unique incident identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>When the incident occurred (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>State where the error occurred.</summary>
    public string State { get; set; } = default!;

    /// <summary>Transition that was executing when the error occurred.</summary>
    public string Transition { get; set; } = default!;

    /// <summary>Task key that failed (null for pipeline-level errors).</summary>
    public string? Task { get; set; }

    /// <summary>Human-readable error message.</summary>
    public string Message { get; set; } = default!;

    /// <summary>Normalized error code (e.g. "Task:Http:503").</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Error layer: "Transport", "Task", or "Pipeline".</summary>
    public string? ErrorLayer { get; set; }

    /// <summary>HTTP status code (when applicable).</summary>
    public int? StatusCode { get; set; }

    /// <summary>Error boundary action taken (e.g. "Abort", "Retry", "Log", "Ignore").</summary>
    public string? BoundaryAction { get; set; }

    /// <summary>Error boundary level that matched (e.g. "Task", "State", "Global").</summary>
    public string? BoundaryLevel { get; set; }

    /// <summary>OpenTelemetry trace identifier for correlation with distributed traces.</summary>
    public string? TraceId { get; set; }

    /// <summary>Whether this incident has been resolved.</summary>
    public bool IsResolved { get; set; }

    /// <summary>When the incident was resolved (UTC). Null if still active.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Number of retry attempts before resolution or exhaustion.</summary>
    public int RetryCount { get; set; }

    public static IncidentDetailDto FromIncident(InstanceIncident incident) => new()
    {
        Id = incident.Id,
        CreatedAt = incident.CreatedAt,
        State = incident.State,
        Transition = incident.Transition,
        Task = incident.Task,
        Message = incident.Message,
        ErrorCode = incident.ErrorCode,
        ErrorLayer = incident.ErrorLayer,
        StatusCode = incident.StatusCode,
        BoundaryAction = incident.BoundaryAction,
        BoundaryLevel = incident.BoundaryLevel,
        TraceId = incident.TraceId,
        IsResolved = incident.IsResolved,
        ResolvedAt = incident.ResolvedAt,
        RetryCount = incident.RetryCount
    };
}

/// <summary>
/// One page of an instance's incident history, newest first.
/// </summary>
public sealed class GetInstanceIncidentsOutput
{
    /// <summary>Whether the instance currently has an unresolved incident.</summary>
    public bool HasActiveIncident { get; set; }

    /// <summary>Incidents on this page, newest first. Stack traces are never included.</summary>
    public List<IncidentDetailDto> Items { get; set; } = [];

    /// <summary>1-based page number.</summary>
    public int Page { get; set; }

    /// <summary>Page size.</summary>
    public int PageSize { get; set; }

    /// <summary>Whether a next page exists.</summary>
    public bool HasNext { get; set; }
}

/// <summary>
/// Output for instance history (all state transitions)
/// </summary>
public sealed class GetInstanceHistoryOutput
{
    public List<InstanceTransitionDto> Transitions { get; set; } = [];
}

/// <summary>
/// DTO representing a single state transition for instance history responses.
/// </summary>
public sealed class InstanceTransitionDto
{
    /// <summary>Unique transition identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Transition definition key (e.g. "approve", "reject").</summary>
    public string TransitionId { get; set; } = string.Empty;

    /// <summary>State the instance was in when the transition started.</summary>
    public string FromState { get; set; } = string.Empty;

    /// <summary>State the instance moved to. Null when the transition is still in progress.</summary>
    public string? ToState { get; set; }

    /// <summary>Effective (externally visible) state at completion time; may differ from ToState when a subflow is active. Null when the transition is still in progress or failed.</summary>
    public string? EffectiveState { get; set; }

    /// <summary>Type of the effective state at completion time (Initial, Intermediate, Finish, SubFlow, Wizard). Null when the transition is still in progress or failed.</summary>
    public StateType? EffectiveStateType { get; set; }

    /// <summary>Sub type of the effective state at completion time (Success, Error, Human, Timeout, etc.). Null when the transition is still in progress or failed.</summary>
    public StateSubType? EffectiveStateSubType { get; set; }

    /// <summary>Free-form stage label of the instance at completion time. Null when not set or the transition is still in progress.</summary>
    public string? Stage { get; set; }

    /// <summary>UTC timestamp when the transition started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp when the transition completed. Null when still in progress.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Total transition duration in seconds. Null when still in progress.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Trigger type that initiated the transition (Manual, Automatic, Timeout, etc.).</summary>
    public TriggerType TriggerType { get; set; }

    /// <summary>Body payload submitted with the transition.</summary>
    public JsonElement? Body { get; set; }

    /// <summary>Header payload submitted with the transition.</summary>
    public JsonElement? Header { get; set; }

    /// <summary>UTC timestamp when the transition record was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>User identifier that created the transition.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Behalf-of user identifier captured when the transition was created.</summary>
    public string? CreatedByBehalfOf { get; set; }
}

/// <summary>
/// One task journal entry of an instance — execution metadata plus the fault reason. The journaled
/// request/response/invocation payloads are deliberately not exposed on any API: mapping scripts
/// write their built headers into them, so they are operator material (journal table only), not
/// client material.
/// </summary>
public sealed class InstanceTaskDto
{
    /// <summary>Journal row identifier — the <c>taskId</c> the actions function takes.</summary>
    public Guid Id { get; set; }

    /// <summary>Task definition key that was executed.</summary>
    public string TaskKey { get; set; } = string.Empty;

    /// <summary>Definition key of the transition the task ran under.</summary>
    public string TransitionKey { get; set; } = string.Empty;

    /// <summary>State the owning transition started from.</summary>
    public string FromState { get; set; } = string.Empty;

    /// <summary>State the owning transition moved to. Null while that transition is in progress.</summary>
    public string? ToState { get; set; }

    /// <summary>Trigger type of the owning transition (NOT the task's hook — see <see cref="Hook"/>).</summary>
    public TriggerType TriggerType { get; set; }

    /// <summary>
    /// The hook (phase) this task ran under — <c>OnExecute</c> / <c>OnEntry</c> / <c>OnExit</c> etc.
    /// This is what distinguishes a state's OnEntry tasks from the triggering transition's OnExecute
    /// tasks (vnext-client-sdk-core#60). Null on rows journaled before this became a column — the API
    /// reports unknown rather than fabricating.
    /// </summary>
    public Definitions.TaskTrigger? Hook { get; set; }

    /// <summary>The task's declared order within its hook group (equal order ⇒ parallel group). Null for legacy rows.</summary>
    public int? Order { get; set; }

    /// <summary>Platform execution status (Waiting, Busy, Completed, Faulted).</summary>
    public Definitions.TaskStatus Status { get; set; }

    /// <summary>Business outcome (Unknown, Success, Failed) — separate from platform status.</summary>
    public BusinessStatus BusinessStatus { get; set; }

    /// <summary>UTC timestamp when the task started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp when the task finished. Null while still executing.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Total task duration in milliseconds. Null while still executing.</summary>
    public double? DurationMs { get; set; }

    /// <summary>Fault reason of a Faulted task. Null otherwise; never a stack trace.</summary>
    public string? Error { get; set; }

    public static InstanceTaskDto FromRow(InstanceTaskHistoryRow row) => new()
    {
        Id = row.Id,
        TaskKey = row.TaskKey,
        TransitionKey = row.TransitionKey,
        FromState = row.FromState,
        ToState = row.ToState,
        TriggerType = row.TriggerType,
        Status = row.Status,
        BusinessStatus = row.BusinessStatus,
        StartedAt = row.StartedAt,
        FinishedAt = row.FinishedAt,
        DurationMs = row.Duration?.TotalMilliseconds,
        Error = ExtractFaultReason(row.FaultedResponseJson),
        Hook = row.Hook,
        Order = row.Order
    };

    /// <summary>
    /// A faulted journal row stores its reason as <c>{"error": "..."}</c> in Response
    /// (<see cref="InstanceTask.Faulted"/>); the projection carries that column for faulted rows
    /// only, and nothing else from the payload is surfaced here. Shared with the metrics DTOs, which
    /// read the same faulted-Response column.
    /// </summary>
    internal static string? ExtractFaultReason(string? faultedResponseJson)
    {
        if (string.IsNullOrWhiteSpace(faultedResponseJson))
            return null;

        using var response = JsonDocument.Parse(faultedResponseJson);
        return response.RootElement.ValueKind == JsonValueKind.Object &&
               response.RootElement.TryGetProperty("error", out var reason) &&
               reason.ValueKind == JsonValueKind.String
            ? reason.GetString()
            : null;
    }
}

/// <summary>
/// The instance's task execution history, in execution order (oldest first).
/// </summary>
public sealed class GetInstanceTasksOutput
{
    /// <summary>All task journal entries of the instance, oldest first. Metadata only — no payloads.</summary>
    public List<InstanceTaskDto> Items { get; set; } = [];
}

/// <summary>
/// One recorded execution sub-step (action) of a task journal entry.
/// </summary>
public sealed class InstanceTaskActionDto
{
    /// <summary>Unique action identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Free-form sub-step status label.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the sub-step started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp when the sub-step finished. Null while open.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Sub-step duration in milliseconds. Null while open.</summary>
    public double? DurationMs { get; set; }

    /// <summary>Structured detail recorded with the sub-step.</summary>
    public JsonElement? Detail { get; set; }

    public static InstanceTaskActionDto FromAction(InstanceAction action) => new()
    {
        Id = action.Id,
        Status = action.Status,
        StartedAt = action.StartedAt,
        FinishedAt = action.FinishedAt,
        DurationMs = action.Duration?.TotalMilliseconds,
        Detail = action.Detail?.JsonElement
    };
}

/// <summary>
/// The recorded actions of one task journal entry, in execution order (oldest first).
/// </summary>
public sealed class GetInstanceTaskActionsOutput
{
    /// <summary>Journal row identifier of the owning task.</summary>
    public Guid TaskId { get; set; }

    /// <summary>Task definition key of the owning task.</summary>
    public string TaskKey { get; set; } = string.Empty;

    /// <summary>All recorded actions of the task, oldest first.</summary>
    public List<InstanceTaskActionDto> Items { get; set; } = [];
}

/// <summary>
/// Identifies what a metrics response is grouped over — a transition definition or a state.
/// </summary>
/// <remarks>
/// One grammar for both endpoints: <see cref="Kind"/> is <c>"transition"</c> or <c>"state"</c> and
/// <see cref="Key"/> echoes the key from the route, so a client that fetched by state and one that
/// fetched by transition parse the same shape.
/// </remarks>
public sealed class MetricsElementDto
{
    /// <summary>Either <c>"transition"</c> or <c>"state"</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The transition or state key this response is about (echoes the route).</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>
/// One task that ran within an attempt — the journaled metadata only. Payloads
/// (<c>Request</c>/<c>Response</c>/<c>InvocationResult</c>) are deliberately never surfaced (auth
/// material), exactly as on the tasks function; <see cref="Error"/> is the one payload-derived field,
/// the fault reason of a faulted row.
/// </summary>
public sealed class MetricsTaskDto
{
    /// <summary>Journal row identifier (the <c>taskId</c> the actions function takes).</summary>
    public Guid Id { get; set; }

    /// <summary>Task definition key that was executed.</summary>
    public string TaskKey { get; set; } = string.Empty;

    /// <summary>
    /// The hook (phase) this task ran under — <c>OnExecute</c> / <c>OnEntry</c> / <c>OnExit</c>. This
    /// is what a client groups by (vnext-client-sdk-core#60). Null on rows journaled before this
    /// became a column — reported as unknown rather than fabricated.
    /// </summary>
    public Definitions.TaskTrigger? Hook { get; set; }

    /// <summary>The task's declared order within its hook group (equal order ⇒ parallel group). Null for legacy rows.</summary>
    public int? Order { get; set; }

    /// <summary>Platform execution status (Waiting, Busy, Completed, Faulted). A parallel sibling of a failed task can stay <c>Waiting</c> — surfaced, not hidden.</summary>
    public Definitions.TaskStatus Status { get; set; }

    /// <summary>Business outcome (Unknown, Success, Failed) — separate from platform status.</summary>
    public BusinessStatus BusinessStatus { get; set; }

    /// <summary>UTC timestamp when the task started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Task duration in milliseconds. Null while still executing (e.g. a half <c>Waiting</c> row).</summary>
    public double? DurationMs { get; set; }

    /// <summary>
    /// Reference to the task whose failure faulted this one (<c>FaultedTaskId</c>). Always null today —
    /// the runtime has never had a writer for it — but carried so the shape is stable when one lands.
    /// </summary>
    public Guid? FaultedTaskRef { get; set; }

    /// <summary>Fault reason of a Faulted task. Null otherwise; never a stack trace.</summary>
    public string? Error { get; set; }

    public static MetricsTaskDto FromRow(InstanceTaskMetricsRow row) => new()
    {
        Id = row.Id,
        TaskKey = row.TaskKey,
        Hook = row.Hook,
        Order = row.Order,
        Status = row.Status,
        BusinessStatus = row.BusinessStatus,
        StartedAt = row.StartedAt,
        DurationMs = row.Duration?.TotalMilliseconds,
        FaultedTaskRef = row.FaultedTaskId,
        Error = InstanceTaskDto.ExtractFaultReason(row.FaultedResponseJson)
    };
}

/// <summary>
/// One firing (transition) or one visit (state) — an "attempt" — with the tasks that ran under it.
/// </summary>
/// <remarks>
/// The same shape serves both endpoints, but two fields read differently by kind and the difference
/// is deliberate:
/// <list type="bullet">
/// <item><b>transition</b>: an attempt is one <c>InstanceTransitions</c> row for the key.
/// <see cref="DurationMs"/> is that row's execution duration (not state dwell); <see cref="TriggerType"/>
/// / <see cref="TriggeredBy"/> describe that firing; <see cref="Tasks"/> are every task journaled
/// under it (onExecute of the transition plus the adjacent states' onExit/onEntry — the
/// <see cref="MetricsTaskDto.Hook"/> tells them apart).</item>
/// <item><b>state</b>: an attempt is one visit (entry→exit). <see cref="StartedAt"/> is when the state
/// was entered and <see cref="FinishedAt"/> when it was left (null while still in the state);
/// <see cref="DurationMs"/> is therefore the <i>dwell</i>; <see cref="TriggerType"/> /
/// <see cref="TriggeredBy"/> describe the transition that entered the state; <see cref="Tasks"/> are
/// the state's onEntry tasks (from the entering transition) followed by its onExit tasks (from the
/// leaving transition).</item>
/// </list>
/// </remarks>
public sealed class MetricsAttemptDto
{
    /// <summary>1-based sequence of this attempt in time order (oldest first).</summary>
    public int Seq { get; set; }

    /// <summary>When the firing started (transition) / when the state was entered (state visit).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When the firing finished (transition) / when the state was left (state visit). Null while in progress or still in the state.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Execution duration in ms (transition) / dwell in ms (state visit). Null while in progress or still in the state.</summary>
    public double? DurationMs { get; set; }

    /// <summary>Trigger type of the firing (transition) / of the transition that entered the state (state visit).</summary>
    public TriggerType TriggerType { get; set; }

    /// <summary>User that triggered the firing / entered the state (the transition's <c>CreatedBy</c>).</summary>
    public string? TriggeredBy { get; set; }

    /// <summary>Tasks that ran under this attempt, in execution order.</summary>
    public List<MetricsTaskDto> Tasks { get; set; } = [];
}

/// <summary>
/// The attempts model for one transition key or one state — the target of a click-to-fetch metrics
/// call (vnext-client-sdk-core#60, item B). Read-only over the already-journaled
/// <c>InstanceTransitions</c> / <c>InstanceTasks</c> rows; no new write path.
/// </summary>
public sealed class GetInstanceMetricsOutput
{
    /// <summary>What this response is grouped over (transition or state, and its key).</summary>
    public MetricsElementDto Element { get; set; } = new();

    /// <summary>Number of attempts (== <see cref="Attempts"/> count; a convenience for the client).</summary>
    public int Count { get; set; }

    /// <summary>Every attempt, oldest first. All firings/visits are returned; filtering is the client's choice.</summary>
    public List<MetricsAttemptDto> Attempts { get; set; } = [];
}

/// <summary>
/// Output for instance data
/// </summary>
public sealed class GetInstanceDataOutput
{
    public JsonElement? Data { get; set; }

    /// <summary>
    /// ETag value returned with quotes per RFC 7232.
    /// </summary>
    public string? ETag
    {
        get
        {
            if (string.IsNullOrEmpty(_etag))
                return null;
            // Strip any existing quotes and wrap with quotes per RFC 7232
            var unquoted = _etag.Replace("\"", "");
            return $"\"{unquoted}\"";
        }
        set => _etag = value;
    }
    private string? _etag = string.Empty;

    /// <summary>
    /// Entity (DB row) version for concurrency and write operations, returned with quotes per RFC 7232. Exposed as X-Entity-ETag response header.
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

    public Dictionary<string, object>? Extensions { get; set; }
}

/// <summary>
/// A human-task candidate row, as selected from one workflow schema.
/// </summary>
/// <remarks>
/// Deliberately not an <c>Instance</c>. The candidate scan answers only "which roots are waiting,
/// and in what order" — the identity and the ordering key. Everything the response actually says
/// about the task comes from the LEAF, which the descent loads once, by id. Selecting whole
/// aggregates here hydrated <c>DataList</c> and the correlations for every candidate and then threw
/// them away, and a <c>SELECT *</c> also puts the covering columns of
/// <c>IX_Instances_HumanTaskV2</c> out of the planner's reach.
/// </remarks>
/// <param name="Id">The instance's own identifier — unique, and how the descent addresses it.</param>
/// <param name="Key">
/// The business key. Its OWN only for a root: <c>SubflowStarter</c> gives every child
/// <c>Key = parentInstance.Key</c>, so a SubProcess carries a key that belongs to the case it was
/// spawned from, not to itself.
/// </param>
/// <param name="Type">
/// <c>R</c> or <c>P</c> — which of the two the row is. A SubProcess is fire-and-forget: nothing
/// waits for it and nothing projects its state upward, so it is an independent unit of work that
/// must be addressed by its own identity rather than by the key it inherited.
/// </param>
/// <param name="CreatedAt">Creation time; the list's sort key.</param>
/// <param name="Flow">
/// The flow the row was selected from. Carried on the row because the scan reads every flow of the
/// domain in ONE statement: after the union the rows are otherwise indistinguishable, and the
/// descent has to re-enter each candidate's own flow to resolve its leaf.
/// </param>
public sealed record HumanTaskCandidate(
    Guid Id, string? Key, InstanceType Type, DateTime CreatedAt, string Flow)
{
    /// <summary>
    /// What a client can actually address this row by: the business key when the instance owns one,
    /// and the instance's own id when it does not.
    /// </summary>
    /// <remarks>
    /// Emitting the inherited key for a SubProcess is not merely ambiguous, it is wrong twice over:
    /// two rows of one case collide on it, and following it leads to the PARENT instance, not to
    /// the SubProcess holding the task.
    /// </remarks>
    public string AddressableId =>
        Type.Equals(InstanceType.SubProcess) || string.IsNullOrEmpty(Key)
            ? Id.ToString()
            : Key;
}

/// <summary>
/// Output for the human-task function: represents an active instance pending human action.
/// </summary>
public sealed class HumanTaskItemOutput
{
    /// <summary>
    /// Workflow instance identifier — the business key, which is what a client addresses.
    /// Not unique on its own: a SubProcess child inherits its parent's key, so two rows of one
    /// case can carry the same value. Use <see cref="Id"/> to tell them apart.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// The instance's own identifier. Always unique, unlike <see cref="InstanceId"/>.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Workflow key (schema) this instance belongs to.</summary>
    public string? Workflow { get; set; }

    /// <summary>Human task title extracted from instance data.</summary>
    public string? Title { get; set; }

    /// <summary>Human task description extracted from instance data.</summary>
    public string? Description { get; set; }

    /// <summary>When the instance was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Static flag indicating this is a vNext task.</summary>
    public bool VNext { get; set; } = true;
}