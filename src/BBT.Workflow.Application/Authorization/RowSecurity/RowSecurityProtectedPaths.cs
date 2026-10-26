using BBT.Workflow.Definitions.RowSecurity;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// How a row-security rule leaf relates to a protected master-schema path, ordered by precedence:
/// when a leaf has several values (sources), the highest member wins.
/// </summary>
public enum RowSecurityPathProtection
{
    /// <summary>The path is not protected for this source.</summary>
    None = 0,

    /// <summary>A <c>Headers</c> parameter on an <c>x-roles</c> path: accepted, warned (RS-W6r).</summary>
    HeaderOnRoleRestricted = 1,

    /// <summary>A <c>QueryParameters</c> parameter on an <c>x-roles</c> path: refused (RS-E18).</summary>
    QueryOnRoleRestricted = 2,

    /// <summary>An <c>x-masking</c> path: refused whatever the source (RS-E15).</summary>
    Masked = 3,

    /// <summary>An <c>x-encryption: hash</c> path: refused whatever the source (RS-E14).</summary>
    Hashed = 4,

    /// <summary>An <c>x-encryption: encrypt</c> path: refused whatever the source (RS-E08).</summary>
    Encrypted = 5
}

/// <summary>
/// The single classifier of row-security leaves against the master schema's protected paths
/// (<c>x-roles</c>, <c>x-masking</c>, <c>x-encryption</c>). Used by the publish check
/// (<see cref="RowSecurityPublishCheck"/>), where a refusal is a publish error, and by the read-time
/// planner backstop, where the same refusal drops the rule (publish order may have skipped the publish
/// check, RS-W0).
/// </summary>
/// <remarks>
/// A leaf relates to a protected path P when its field equals P, sits under P, or is an ancestor of P
/// (an object containment on an ancestor reaches P's value). Precedence follows
/// <see cref="RowSecurityPathProtection"/>: encrypt, hash, mask, then the source-dependent <c>x-roles</c>
/// verdicts. <c>CurrentUser</c> values and literals are trusted, so an <c>x-roles</c> path alone never
/// refuses them.
/// </remarks>
public static class RowSecurityProtectedPaths
{
    /// <summary>
    /// Classifies one leaf value against the protected paths of <paramref name="exposure"/>.
    /// </summary>
    /// <param name="field">The leaf's data path (dot-separated). Pass data paths only: instance columns
    /// (<see cref="RowTemplateLeaf.IsInstanceColumn"/>) are never schema-protected.</param>
    /// <param name="exposure">The master schema's exposure metadata (<see cref="SchemaRolesParser.ParseExposure"/>).</param>
    /// <param name="source">Where the value is read from; null for a literal.</param>
    /// <returns>The protection verdict; <see cref="RowSecurityPathProtection.None"/> when unprotected.</returns>
    public static RowSecurityPathProtection Classify(
        string field, SchemaExposureMetadata exposure, RowParameterSource? source)
    {
        if (string.IsNullOrEmpty(field) || exposure.IsEmpty)
            return RowSecurityPathProtection.None;

        if (AnyRelated(field, exposure.EncryptPaths))
            return RowSecurityPathProtection.Encrypted;

        if (AnyRelated(field, exposure.HashPaths.Keys))
            return RowSecurityPathProtection.Hashed;

        if (AnyRelated(field, exposure.PathMaskRules.Where(r => !r.Value.IsEncryption).Select(r => r.Key)))
            return RowSecurityPathProtection.Masked;

        if (source is RowParameterSource.QueryParameters or RowParameterSource.Headers &&
            AnyRelated(field, exposure.PathRoleGrants.Keys))
        {
            return source == RowParameterSource.QueryParameters
                ? RowSecurityPathProtection.QueryOnRoleRestricted
                : RowSecurityPathProtection.HeaderOnRoleRestricted;
        }

        return RowSecurityPathProtection.None;
    }

    /// <summary>True when <paramref name="protection"/> refuses the leaf (publish error, read-time drop).</summary>
    /// <param name="protection">The verdict from <see cref="Classify"/>.</param>
    public static bool IsRefused(RowSecurityPathProtection protection) =>
        protection >= RowSecurityPathProtection.QueryOnRoleRestricted;

    /// <summary>The <c>RS-*</c> code for <paramref name="protection"/>; null for <see cref="RowSecurityPathProtection.None"/>.</summary>
    /// <param name="protection">The verdict from <see cref="Classify"/>.</param>
    public static string? CodeOf(RowSecurityPathProtection protection) => protection switch
    {
        RowSecurityPathProtection.Encrypted => RowSecurityErrorCodes.E08,
        RowSecurityPathProtection.Hashed => RowSecurityErrorCodes.E14,
        RowSecurityPathProtection.Masked => RowSecurityErrorCodes.E15,
        RowSecurityPathProtection.QueryOnRoleRestricted => RowSecurityErrorCodes.E18,
        RowSecurityPathProtection.HeaderOnRoleRestricted => "RS-W6r",
        _ => null
    };

    private static bool AnyRelated(string field, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (field.Equals(path, StringComparison.Ordinal) ||
                field.StartsWith(path + ".", StringComparison.Ordinal) ||
                path.StartsWith(field + ".", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
