using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Per-state override configuration for a SubFlow state, keyed by the CHILD's state key.
/// <see cref="QueryRoles"/> replaces the state's query grants; <see cref="Interaction"/> is a
/// field-level override of the state's long-poll; <see cref="Views"/> swaps the view the child's own
/// rules selected in this state. Stamped onto the child at start and resolved by the child.
/// </summary>
public sealed class SubFlowStateOverride
{
    private SubFlowStateOverride()
    {
    }

    [JsonConstructor]
    private SubFlowStateOverride(
        List<RoleGrant>? queryRoles,
        SubFlowStateInteractionOverride? interaction,
        Dictionary<string, Reference>? views)
    {
        QueryRoles = queryRoles;
        Interaction = interaction;
        Views = views;
    }

    /// <summary>
    /// State query role overrides (replace mode). DENY always overrides ALLOW.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("queryRoles")]
    public List<RoleGrant>? QueryRoles { get; private set; }

    /// <summary>
    /// Field-level override of the child state's <c>interaction</c>. See <see cref="SubFlowLongPollOverride"/>.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("interaction")]
    public SubFlowStateInteractionOverride? Interaction { get; private set; }

    /// <summary>
    /// View swap for this child state: key = the view key the child's rules selected, value = the
    /// replacement view. Rules are never overridden — only the selected view's reference is replaced.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("views")]
    public Dictionary<string, Reference>? Views { get; private set; }

    public static SubFlowStateOverride Create(
        List<RoleGrant>? queryRoles = null,
        SubFlowStateInteractionOverride? interaction = null,
        Dictionary<string, Reference>? views = null)
        => new(queryRoles, interaction, views);
}
