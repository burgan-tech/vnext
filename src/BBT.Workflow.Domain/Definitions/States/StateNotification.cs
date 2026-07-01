using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Declarative state-level notification entry. States declare a <c>notifications</c> array; each
/// entry is dispatched (when applicable) once the transition pipeline settles and the instance
/// state/status is finalized.
/// Mirrors the <c>NotificationTask</c> state-channel behaviour — same <see cref="ScriptCode"/>
/// mapping (compiled to an <c>IStateNotificationMapping</c>) and the same Dapr
/// <c>vnext-notification-state</c> binding — but is triggered automatically after settle.
/// An optional <see cref="Rule"/> (compiled to an <c>IConditionMapping</c>) gates dispatch: when the
/// rule is absent the entry is always processed; when present, only entries whose rule evaluates to
/// <c>true</c> are processed.
/// </summary>
public sealed class StateNotification
{
    private StateNotification()
    {
    }

    [JsonConstructor]
    private StateNotification(StateNotificationType type, ScriptCode? rule, ScriptCode? mapping)
    {
        Type = type;
        Rule = rule;
        Mapping = mapping;
    }

    /// <summary>
    /// Notification kind. Defaults to <see cref="StateNotificationType.State"/> when the <c>type</c>
    /// field is omitted. Only <c>state</c> is processed today.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("type")]
    public StateNotificationType Type { get; private set; }

    /// <summary>
    /// Optional dispatch rule. Compiled to an <c>IConditionMapping</c> and evaluated against the
    /// settled instance's <c>ScriptContext</c>. Null/empty means always process this entry.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("rule")]
    public ScriptCode? Rule { get; private set; }

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
    /// True when a dispatch rule with an executable body is configured.
    /// </summary>
    [JsonIgnore]
    public bool HasRule => Rule?.HasMappingCode == true;

    /// <summary>
    /// Creates a new <see cref="StateNotification"/>.
    /// </summary>
    public static StateNotification Create(
        ScriptCode? mapping,
        ScriptCode? rule = null,
        StateNotificationType type = StateNotificationType.State) =>
        new(type, rule, mapping);
}
