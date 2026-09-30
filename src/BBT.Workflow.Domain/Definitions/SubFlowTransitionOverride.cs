using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Per-transition override configuration for a SubFlow state, keyed by the CHILD's transition key.
/// <see cref="Roles"/> replaces the transition's grants; <see cref="Views"/> swaps the transition view
/// the child's own rules selected. Stamped onto the child at start and resolved by the child.
/// </summary>
public sealed class SubFlowTransitionOverride
{
    private SubFlowTransitionOverride()
    {
    }

    [JsonConstructor]
    private SubFlowTransitionOverride(List<RoleGrant>? roles, Dictionary<string, Reference>? views)
    {
        Roles = roles;
        Views = views;
    }

    /// <summary>
    /// Transition role overrides (replace mode). DENY always overrides ALLOW.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("roles")]
    public List<RoleGrant>? Roles { get; private set; }

    /// <summary>
    /// Transition view swap: key = the view key the child's rules selected for this transition,
    /// value = the replacement view.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("views")]
    public Dictionary<string, Reference>? Views { get; private set; }

    public static SubFlowTransitionOverride Create(
        List<RoleGrant>? roles = null,
        Dictionary<string, Reference>? views = null)
        => new(roles, views);
}
