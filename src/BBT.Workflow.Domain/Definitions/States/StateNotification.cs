using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Declarative state-level notification directive. When a state defines this object, the engine
/// dispatches a state notification (via the platform-managed <c>state</c> channel) once the
/// transition pipeline settles and the instance state/status is finalized.
/// Mirrors the <c>NotificationTask</c> state-channel behaviour — same <see cref="ScriptCode"/>
/// mapping (compiled to an <c>IStateNotificationMapping</c>) and the same Dapr
/// <c>vnext-notification-state</c> binding — but is triggered automatically after settle rather
/// than as an OnEntry/OnExit/OnExecute task.
/// </summary>
public sealed class StateNotification
{
    private StateNotification()
    {
    }

    [JsonConstructor]
    private StateNotification(ScriptCode mapping)
    {
        Mapping = mapping;
    }

    /// <summary>
    /// State Notify Mapping. Compiled to an <c>IStateNotificationMapping</c> to enrich the state
    /// notification metadata and override the binding operation. Optional: when absent, default
    /// metadata is used.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("mapping")]
    public ScriptCode? Mapping { get; private set; }

    /// <summary>
    /// True when a mapping with an executable body is configured.
    /// </summary>
    [JsonIgnore]
    public bool HasMapping => Mapping?.HasMappingCode == true;

    /// <summary>
    /// Creates a new <see cref="StateNotification"/> with the supplied mapping.
    /// </summary>
    public static StateNotification Create(ScriptCode mapping) => new(mapping);
}
