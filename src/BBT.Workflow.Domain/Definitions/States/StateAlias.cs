using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Role-aware display alias for a state. Lets the same internal state be presented
/// under different, role-appropriate labels without changing the workflow's real state identity.
/// JSON format: { "name": "Değerlendirme Aşamasında", "roles": [ { "role": "...", "grant": "allow" } ] }.
/// <para>
/// Aliases are evaluated in declaration order, first match wins. An entry with an empty
/// <c>roles</c> list matches everyone and therefore acts as a default/fallback — place it last.
/// </para>
/// </summary>
public sealed class StateAlias
{
    private const int MaxNameLength = 180;

    private StateAlias()
    {
    }

    [JsonConstructor]
    internal StateAlias(string name, List<RoleGrant>? roles)
    {
        Name = Check.NotNullOrWhiteSpace(name, nameof(Name), MaxNameLength);
        this.roles = roles ?? [];
    }

    /// <summary>
    /// Display label returned in the state representation when this alias resolves for the caller.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    [JsonInclude] [JsonPropertyName("roles")]
    private List<RoleGrant> roles = new();

    /// <summary>
    /// Role grants that must resolve to the caller for this alias to apply.
    /// Empty means the alias matches everyone (default/fallback). DENY always wins; otherwise any ALLOW match applies.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<RoleGrant> Roles => roles.AsReadOnly();

    /// <summary>
    /// Creates a new state alias with the given display name and role grants.
    /// </summary>
    public static StateAlias Create(string name, List<RoleGrant>? roles = null)
    {
        return new StateAlias(name, roles);
    }
}
</content>
</invoke>
