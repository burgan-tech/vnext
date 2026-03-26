using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Per-state override configuration for a SubFlow state.
/// Replace mode: when present, the SubFlow's own state configuration is ignored for that state.
/// </summary>
public sealed class SubFlowStateOverride
{
    private SubFlowStateOverride()
    {
    }

    [JsonConstructor]
    private SubFlowStateOverride(List<RoleGrant>? queryRoles)
    {
        QueryRoles = queryRoles;
    }

    /// <summary>
    /// State query role overrides (replace mode). DENY always overrides ALLOW.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("queryRoles")]
    public List<RoleGrant>? QueryRoles { get; private set; }

    public static SubFlowStateOverride Create(List<RoleGrant>? queryRoles = null) => new(queryRoles);
}
