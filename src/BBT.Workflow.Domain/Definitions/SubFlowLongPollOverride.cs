using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Parent-supplied override of a child state's <c>interaction.longPoll</c>. Field-level replace: a
/// field that is null keeps the child's own value; a field that is present replaces it. Only the
/// acknowledge window and the role grants are overridable — <c>terminate</c> and the <c>rule</c>
/// arm stay the child author's, and an override never adds a long-poll to a state that declares none.
/// </summary>
public sealed class SubFlowLongPollOverride
{
    private SubFlowLongPollOverride()
    {
    }

    [JsonConstructor]
    private SubFlowLongPollOverride(int? fallbackTimeoutSeconds, List<RoleGrant>? roles)
    {
        FallbackTimeoutSeconds = fallbackTimeoutSeconds;
        Roles = roles;
    }

    /// <summary>
    /// Acknowledge fallback window in seconds. Null keeps the child's window.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("fallbackTimeoutSeconds")]
    public int? FallbackTimeoutSeconds { get; private set; }

    /// <summary>
    /// Role grants replacing the child's <c>interaction.longPoll.roles</c> as a whole list (no grant
    /// merge). Null keeps the child's grants; an empty list means default-allow. Ignored when the
    /// child authorizes the interaction with a <c>rule</c>.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("roles")]
    public List<RoleGrant>? Roles { get; private set; }

    /// <summary>
    /// True when the override carries nothing to apply.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => FallbackTimeoutSeconds is null && Roles is null;

    public static SubFlowLongPollOverride Create(int? fallbackTimeoutSeconds = null, List<RoleGrant>? roles = null)
        => new(fallbackTimeoutSeconds, roles);
}
