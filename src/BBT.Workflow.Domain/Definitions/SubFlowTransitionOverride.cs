using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Per-transition override configuration for a SubFlow state.
/// Replace mode: when present, the SubFlow's own transition configuration is ignored for that transition.
/// </summary>
public sealed class SubFlowTransitionOverride
{
    private SubFlowTransitionOverride()
    {
    }

    [JsonConstructor]
    private SubFlowTransitionOverride(List<RoleGrant>? roles)
    {
        Roles = roles;
    }

    /// <summary>
    /// Transition role overrides (replace mode). DENY always overrides ALLOW.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("roles")]
    public List<RoleGrant>? Roles { get; private set; }

    public static SubFlowTransitionOverride Create(List<RoleGrant>? roles = null) => new(roles);
}
