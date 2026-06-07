using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Role-aware display alias for a state. Lets the same internal state be presented
/// under different, role-appropriate labels without changing the workflow's real state identity.
/// JSON format:
/// { "name": "Application Under Review",
///   "roles": [ { "role": "...", "grant": "allow" } ],
///   "labels": [ { "label": "...", "language": "tr" } ] }.
/// <para>
/// Aliases are evaluated in declaration order, first match wins. Authoring rules
/// (enforced by <c>WorkflowValidator</c>): each entry must declare a <c>name</c>, at least one
/// <c>roles</c> grant, and at least one <c>labels</c> entry.
/// </para>
/// </summary>
public sealed class StateAlias
{
    private const int MaxNameLength = 180;

    private StateAlias()
    {
    }

    [JsonConstructor]
    internal StateAlias(string name, List<RoleGrant>? roles, List<LanguageLabel>? labels)
    {
        Name = Check.NotNullOrWhiteSpace(name, nameof(Name), MaxNameLength);
        this.roles = roles ?? [];
        this.labels = labels ?? [];
    }

    /// <summary>
    /// Display label returned when this alias resolves for the caller and no localized label matches.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    [JsonInclude] [JsonPropertyName("roles")]
    private List<RoleGrant> roles = new();

    [JsonInclude] [JsonPropertyName("labels")]
    private List<LanguageLabel> labels = new();

    /// <summary>
    /// Role grants that must resolve to the caller for this alias to apply (at least one is required by
    /// validation). DENY always wins; otherwise any ALLOW match applies. The runtime treats an empty list
    /// defensively as "matches everyone".
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<RoleGrant> Roles => roles.AsReadOnly();

    /// <summary>
    /// Localized display labels. When present, the label for the caller's current language is
    /// returned (with fallback) instead of <see cref="Name"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<LanguageLabel> Labels => labels.AsReadOnly();

    /// <summary>
    /// Creates a new state alias with the given display name, role grants and localized labels.
    /// </summary>
    public static StateAlias Create(
        string name,
        List<RoleGrant>? roles = null,
        List<LanguageLabel>? labels = null)
    {
        return new StateAlias(name, roles, labels);
    }
}
