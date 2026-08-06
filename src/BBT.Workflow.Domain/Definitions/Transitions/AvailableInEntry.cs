using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow.Definitions;

/// <summary>
/// One entry of a transition's <c>availableIn</c> list: the state the transition is offered in,
/// optionally narrowed to a set of role grants that apply only in that state.
/// <para>
/// Authorable in two shapes — a bare state key (<c>"review"</c>) or an object
/// (<c>{ "state": "approval", "roles": [ ... ] }</c>) — handled by
/// <see cref="AvailableInJsonConverter"/>. The bare form is exactly equivalent to the object form
/// with no roles, so definitions written before per-state role scoping behave identically.
/// </para>
/// <para>
/// <see cref="Roles"/> composes with <c>Transition.Roles</c> as an <b>AND</b>: the transition-level
/// grants are the global gate and these are an additional, state-specific narrowing. A caller must
/// satisfy both to be offered the transition.
/// </para>
/// </summary>
public sealed class AvailableInEntry
{
    private const int MaxStateLength = 100;

    [JsonInclude]
    [JsonPropertyName("roles")]
    private List<RoleGrant> roles = [];

    private AvailableInEntry()
    {
    }

    [JsonConstructor]
    internal AvailableInEntry(string state, List<RoleGrant>? roles)
    {
        State = Check.NotNullOrWhiteSpace(state, nameof(State), MaxStateLength);
        this.roles = roles ?? [];
    }

    /// <summary>
    /// State key this entry applies to.
    /// </summary>
    public string State { get; private set; } = string.Empty;

    /// <summary>
    /// Role grants that apply only while the instance is in <see cref="State"/>.
    /// Empty means the state carries no additional narrowing — identical to the bare string form.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<RoleGrant> Roles => roles;

    /// <summary>
    /// True when this entry narrows availability by role in addition to by state.
    /// Also the discriminator <see cref="AvailableInJsonConverter"/> uses to decide whether to write
    /// the bare string shape or the object shape.
    /// </summary>
    [JsonIgnore]
    public bool HasRoles => roles.Count > 0;

    /// <summary>
    /// Creates an entry from a bare state key — the legacy <c>availableIn: ["review"]</c> form.
    /// </summary>
    public static AvailableInEntry FromState(string state) => new(state, null);

    /// <summary>
    /// Creates an entry narrowing a state to a set of role grants.
    /// </summary>
    public static AvailableInEntry FromState(string state, IEnumerable<RoleGrant>? roles) =>
        new(state, roles?.ToList());
}
