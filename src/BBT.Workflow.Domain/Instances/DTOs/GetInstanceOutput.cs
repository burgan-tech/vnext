using System.Text.Json;

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
    public string? Etag
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
    public List<string>? Tags { get; set; } = [];
    public JsonElement? Attributes { get; set; }
    public Dictionary<string, object>? Extensions { get; set; }
}

/// <summary>
/// Output for instance history (all data transitions)
/// </summary>
public sealed class GetInstanceHistoryOutput
{
    public List<GetInstanceOutput> Transitions { get; set; } = [];
}

/// <summary>
/// Single transition execution record for API response.
/// </summary>
public sealed class InstanceTransitionItemOutput
{
    /// <summary>Transition record ID.</summary>
    public Guid Id { get; set; }

    /// <summary>Workflow instance ID.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>Transition key (e.g. transition name).</summary>
    public string TransitionId { get; set; } = string.Empty;

    /// <summary>State before the transition.</summary>
    public string FromState { get; set; } = string.Empty;

    /// <summary>State after the transition (null if not yet completed).</summary>
    public string? ToState { get; set; }

    /// <summary>When the transition started (UTC).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When the transition finished (UTC); null if not completed.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Duration of the transition; null if not completed.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Transition body payload.</summary>
    public JsonElement? Body { get; set; }

    /// <summary>Transition header payload.</summary>
    public JsonElement? Header { get; set; }
}

/// <summary>
/// Output for instance transition records (execution log) by instance.
/// </summary>
public sealed class GetInstanceTransitionsOutput
{
    /// <summary>HATEOAS links (e.g. self, instance).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("links")]
    public object? Links { get; set; }

    /// <summary>List of transition records for the instance, ordered by StartedAt.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public List<InstanceTransitionItemOutput> Items { get; set; } = [];
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
    public string? Etag
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

    public Dictionary<string, object>? Extensions { get; set; }
}