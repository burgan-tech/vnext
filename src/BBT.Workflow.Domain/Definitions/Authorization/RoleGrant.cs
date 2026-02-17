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
/// JSON format: { "role": "morph-idm.maker", "grant": "allow" }.
/// Evaluation rule: DENY always wins; if no DENY match, then any ALLOW match → allow, else deny.
/// </summary>
public sealed class RoleGrant
{
    private const int MaxRoleLength = 180;

    private RoleGrant()
    {
    }

    [JsonConstructor]
    internal RoleGrant(string role, string grant)
    {
        Role = Check.NotNullOrWhiteSpace(role, nameof(Role), MaxRoleLength);
        Grant = GrantKind.FromCode(grant);
    }

    /// <summary>
    /// Role identifier (e.g. morph-idm.maker, domain.rolename).
    /// </summary>
    public string Role { get; private set; } = string.Empty;

    /// <summary>
    /// Grant type: "allow" or "deny". DENY always overrides ALLOW.
    /// </summary>
    public string Grant { get; private set; } = string.Empty;

    public bool IsDeny => GrantKind.IsDeny(Grant);
    public bool IsAllow => GrantKind.IsAllow(Grant);
}
