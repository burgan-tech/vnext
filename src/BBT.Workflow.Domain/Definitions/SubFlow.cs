using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;
public sealed class SubFlow
{
    private SubFlow()
    {
    }

    [JsonConstructor]
    private SubFlow(
        SubFlowType type,
        Reference process,
        ScriptCode mapping,
        Dictionary<string, Reference>? viewOverrides,
        SubFlowOverrides? overrides = null)
    {
        Type = type;
        Process = process;
        Mapping = mapping;
        ViewOverrides = viewOverrides;
        Overrides = overrides;
    }

    public SubFlowType Type { get; private set; }
    public Reference Process { get; private set; }
    public ScriptCode Mapping { get; private set; }

    /// <summary>
    /// Legacy view overrides. Kept for backward compatibility.
    /// When Overrides.Views is also set, Overrides.Views takes precedence.
    /// </summary>
    public Dictionary<string, Reference>? ViewOverrides { get; private set; }

    /// <summary>
    /// Unified override configuration for this SubFlow state.
    /// Supports views, transition roles, query roles, and timeout overrides (all replace mode).
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("overrides")]
    public SubFlowOverrides? Overrides { get; private set; }

    /// <summary>
    /// Returns true if any view override is configured (either legacy ViewOverrides or Overrides.Views).
    /// </summary>
    [NotMapped]
    [JsonIgnore]
    public bool HasViewOverrides => Overrides?.Views != null || ViewOverrides != null;

    /// <summary>
    /// Effective view overrides dictionary: Overrides.Views takes precedence over ViewOverrides.
    /// </summary>
    [NotMapped]
    [JsonIgnore]
    public Dictionary<string, Reference>? EffectiveViewOverrides => Overrides?.Views ?? ViewOverrides;

    [NotMapped]
    [JsonIgnore]
    public bool HasTransitionRoleOverrides => Overrides?.Transitions is { Count: > 0 };

    [NotMapped]
    [JsonIgnore]
    public bool HasQueryRoleOverrides => Overrides?.States is { Count: > 0 };

    [NotMapped]
    [JsonIgnore]
    public bool HasTimeoutOverride => Overrides?.Timeout != null;

    public static SubFlow Create(string type, IReference reference, ScriptCode mapping, Dictionary<string, Reference>? viewOverrides,
        SubFlowOverrides? overrides = null)
    {
        return new SubFlow(SubFlowType.FromCode(type), reference.ToReference(), mapping, viewOverrides, overrides);
    }
}