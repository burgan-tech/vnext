using System.Text.Json;
using BBT.Workflow.Definitions;

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

        if (instance.HasActiveIncident || instance.Incidents.Count > 0)
        {
            Incident = IncidentInfoDto.FromInstance(instance);
        }
    }
    
    /// <summary>Current state key (engine internal state).</summary>
    public string? CurrentState { get; set; }

    /// <summary>Effective state exposed to callers (e.g. SubFlow state when active).</summary>
    public string? EffectiveState { get; set; }

    /// <summary>Instance status (Active, Completed, Faulted, etc.).</summary>
    public InstanceStatus? Status { get; set; }

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

    /// <summary>Error boundary incident information. Null when no incidents have occurred.</summary>
    public IncidentInfoDto? Incident { get; set; }
}

/// <summary>
/// Incident tracking information exposed in instance metadata.
/// </summary>
public sealed class IncidentInfoDto
{
    /// <summary>Whether the instance currently has an unresolved incident.</summary>
    public bool HasActiveIncident { get; set; }

    /// <summary>Total number of incidents (resolved + unresolved) retained for this instance.</summary>
    public int TotalCount { get; set; }

    /// <summary>The most recent unresolved incident, or null if all are resolved.</summary>
    public IncidentDetailDto? Active { get; set; }

    /// <summary>All retained incidents ordered by creation time descending.</summary>
    public List<IncidentDetailDto> History { get; set; } = [];

    internal static IncidentInfoDto FromInstance(Instance instance)
    {
        var incidents = instance.Incidents;
        var active = incidents.LastOrDefault(i => !i.IsResolved);

        var dto = new IncidentInfoDto
        {
            HasActiveIncident = instance.HasActiveIncident,
            TotalCount = incidents.Count,
            Active = active != null ? IncidentDetailDto.FromIncident(active) : null,
            History = incidents
                .OrderByDescending(i => i.CreatedAt)
                .Select(IncidentDetailDto.FromIncident)
                .ToList()
        };

        return dto;
    }
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

    internal static IncidentDetailDto FromIncident(InstanceIncident incident) => new()
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