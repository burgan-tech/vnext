using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Grant type for role-based authorization. DENY always overrides ALLOW.
/// </summary>
public static class GrantKind
{
    public const string Allow = "allow";
    public const string Deny = "deny";

    /// <summary>
    /// Parses grant from JSON string.
    /// </summary>
    public static string FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Grant code is required.", nameof(code));
        var normalized = code.Trim().ToLowerInvariant();
        return normalized switch
        {
            Allow => Allow,
            Deny => Deny,
            _ => throw new ArgumentException($"Unknown grant: {code}. Use '{Allow}' or '{Deny}'.", nameof(code))
        };
    }

    public static bool IsDeny(string grant) => string.Equals(grant, Deny, StringComparison.OrdinalIgnoreCase);
    public static bool IsAllow(string grant) => string.Equals(grant, Allow, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Role-based grant entry for workflow/state/transition/function authorization.
/// JSON formats (exactly one of <c>role</c>, <c>allOf</c>, <c>anyOf</c> per entry):
/// { "role": "morph-idm.maker", "grant": "allow" },
/// { "allOf": [ { "role": "a" }, { "role": "b" } ], "grant": "allow" } (every leaf must match),
/// { "anyOf": [ { "role": "a" }, { "role": "b" } ], "grant": "deny" } (at least one leaf must match).
/// Combinators are one level deep; leaves are plain roles.
/// Evaluation rule: DENY always wins; if no DENY match, then any ALLOW match → allow, else deny.
/// </summary>
public sealed class RoleGrant
{
    internal const int MaxRoleLength = 180;

    private RoleGrant()
    {
    }

    [JsonConstructor]
    internal RoleGrant(
        string? role,
        string grant,
        IReadOnlyList<RoleGrantCondition>? allOf = null,
        IReadOnlyList<RoleGrantCondition>? anyOf = null)
    {
        Grant = GrantKind.FromCode(grant);

        var shapes = (role is not null ? 1 : 0) + (allOf is not null ? 1 : 0) + (anyOf is not null ? 1 : 0);
        if (shapes != 1)
            throw new ArgumentException("A role grant must carry exactly one of 'role', 'allOf' or 'anyOf'.");
        if (allOf is { Count: 0 } || anyOf is { Count: 0 })
            throw new ArgumentException("'allOf' / 'anyOf' must contain at least one { \"role\" } entry.");

        Role = role is null ? null : Check.NotNullOrWhiteSpace(role, nameof(Role), MaxRoleLength);
        AllOf = allOf;
        AnyOf = anyOf;
    }

    /// <summary>
    /// Role identifier (e.g. morph-idm.maker, domain.rolename). Null when the grant is a combinator.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; private set; }

    /// <summary>
    /// Combinator: the grant matches when every leaf matches. Null unless this is an allOf grant.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RoleGrantCondition>? AllOf { get; private set; }

    /// <summary>
    /// Combinator: the grant matches when at least one leaf matches. Null unless this is an anyOf grant.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RoleGrantCondition>? AnyOf { get; private set; }

    /// <summary>
    /// Grant type: "allow" or "deny". DENY always overrides ALLOW.
    /// </summary>
    public string Grant { get; private set; } = string.Empty;

    // Computed projections — never serialized. Without [JsonIgnore] they are written out as
    // "isDeny"/"isAllow" siblings, and the schema declares roleGrant additionalProperties: false, so a
    // re-serialized definition would fail schema re-validation.
    [JsonIgnore] public bool IsDeny => GrantKind.IsDeny(Grant);
    [JsonIgnore] public bool IsAllow => GrantKind.IsAllow(Grant);

    /// <summary>True when the grant is an allOf / anyOf combinator rather than a single role.</summary>
    [JsonIgnore] public bool IsCombinator => Role is null;

    /// <summary>Every role string this grant compares — the single role, or each child's.</summary>
    [JsonIgnore]
    public IEnumerable<string> LeafRoles =>
        Role is not null ? [Role] : (AllOf ?? AnyOf)!.Select(c => c.Role);
}
