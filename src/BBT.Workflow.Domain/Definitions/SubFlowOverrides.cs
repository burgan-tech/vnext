using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Defines parent-level overrides for a SubFlow state.
/// All overrides operate in replace mode: when an override is present, the SubFlow's own configuration
/// is completely ignored for that specific entry.
/// </summary>
public sealed class SubFlowOverrides
{
    private SubFlowOverrides()
    {
    }

    [JsonConstructor]
    private SubFlowOverrides(
        Dictionary<string, Reference>? views,
        Dictionary<string, SubFlowTransitionOverride>? transitions,
        Dictionary<string, SubFlowStateOverride>? states,
        WorkflowTimeout? timeout)
    {
        Views = views;
        Transitions = transitions;
        States = states;
        Timeout = timeout;
    }

    /// <summary>
    /// View overrides: key = SubFlow view key, value = override Reference.
    /// Takes precedence over SubFlow.ViewOverrides when both are set.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("views")]
    public Dictionary<string, Reference>? Views { get; private set; }

    /// <summary>
    /// Transition overrides: key = SubFlow transition key, value = per-transition override config.
    /// Replace mode: when present, the SubFlow's own transition configuration is ignored for that transition.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("transitions")]
    public Dictionary<string, SubFlowTransitionOverride>? Transitions { get; private set; }

    /// <summary>
    /// State overrides: key = SubFlow state key, value = per-state override config.
    /// Replace mode: when present, the SubFlow's own state configuration is ignored for that state.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("states")]
    public Dictionary<string, SubFlowStateOverride>? States { get; private set; }

    /// <summary>
    /// Timeout override: when set, replaces the SubFlow's own workflow-level timeout configuration.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("timeout")]
    public WorkflowTimeout? Timeout { get; private set; }

    public static SubFlowOverrides Create(
        Dictionary<string, Reference>? views = null,
        Dictionary<string, SubFlowTransitionOverride>? transitions = null,
        Dictionary<string, SubFlowStateOverride>? states = null,
        WorkflowTimeout? timeout = null)
        => new(views, transitions, states, timeout);
}
