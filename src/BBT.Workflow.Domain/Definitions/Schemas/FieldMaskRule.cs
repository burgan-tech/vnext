namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// One parsed field-exposure transform of a master-schema property — an <c>x-masking</c> declaration
/// (<c>mask</c>/<c>replace</c>) or an <c>x-encryption</c> declaration of type <c>hash</c> or <c>encrypt</c> — : how
/// the value is transformed on its way out of the runtime, and which callers are exempt from that transform.
/// <para>
/// <c>encrypt</c> is the one rule that also acts on the WRITE path: the value is stored as an AES-256-GCM token
/// (<see cref="BBT.Workflow.Instances.EncryptedValueFormat"/>) and the engine sees plaintext. On the read path an
/// exempt caller sees the plaintext and everyone else the stored token.
/// </para>
/// <para>
/// Masking is a presentation control. It runs after <c>x-roles</c> (a field the caller may not see is
/// pruned and never reaches a mask rule) and only on the caller-facing read surfaces — the data function,
/// the instance GET/list and the sync start/transition response. Stored data is never changed.
/// </para>
/// </summary>
/// <param name="Operator"><see cref="MaskOperator"/> or <see cref="ReplaceOperator"/>.</param>
/// <param name="MaskingChar">Character written for every masked UTF-16 code unit (<c>mask</c> only).</param>
/// <param name="KeepFirst">Leading characters left in the clear (<c>mask</c> only).</param>
/// <param name="KeepLast">Trailing characters left in the clear (<c>mask</c> only).</param>
/// <param name="ReplaceValue">Value written instead of the original (<c>replace</c> only).</param>
/// <param name="ExemptRoles">
/// Allow-only exemption list: a caller matching any of these grants sees the RAW value; every other
/// caller — including one with no roles, one whose roles could not be resolved and one whose role is
/// simply not listed — sees it transformed. Empty means the transform applies to everyone. Always empty for
/// <see cref="HashOperator"/>: hashing happens on write and cannot be undone.
/// </param>
/// <param name="HashAlgorithm"><c>sha256</c> (default) or <c>sha512</c>; <see cref="HashOperator"/> only.</param>
public sealed record FieldMaskRule(
    string Operator,
    string MaskingChar,
    int KeepFirst,
    int KeepLast,
    string? ReplaceValue,
    IReadOnlyList<RoleGrant> ExemptRoles,
    string HashAlgorithm = FieldMaskRule.Sha256)
{
    /// <summary>
    /// One-way digest (<c>x-encryption.type: hash</c>), applied on WRITE: the stored and served value is
    /// <c>HASHED:SHA256:&lt;hex&gt;</c> (HMAC under the instance's own salt). Irreversible — nobody sees the raw value.
    /// </summary>
    public const string HashOperator = "hash";

    /// <summary>Default hash algorithm.</summary>
    public const string Sha256 = "sha256";

    /// <summary>Alternative hash algorithm.</summary>
    public const string Sha512 = "sha512";

    /// <summary>
    /// AES-256-GCM at rest (<c>x-encryption.type: encrypt</c>). The key comes from host configuration, never from
    /// the schema. Exempt callers read the plaintext; everyone else the stored token.
    /// </summary>
    public const string EncryptOperator = "encrypt";

    /// <summary>True for the transform declared under <c>x-encryption</c> (as opposed to <c>x-masking</c>).</summary>
    public bool IsEncryption => Operator is HashOperator or EncryptOperator;

    /// <summary>True for <see cref="EncryptOperator"/>.</summary>
    public bool IsAtRestEncryption => Operator == EncryptOperator;

    /// <summary>Keeps <see cref="KeepFirst"/>/<see cref="KeepLast"/> characters and masks the middle.</summary>
    public const string MaskOperator = "mask";

    /// <summary>Writes <see cref="ReplaceValue"/> instead of the original value.</summary>
    public const string ReplaceOperator = "replace";

    /// <summary>Default masking character.</summary>
    public const string DefaultMaskingChar = "*";
}

/// <summary>
/// Everything the read surfaces need from a master schema to decide what a caller is shown: the
/// <c>x-roles</c> grants (hide) and the <c>x-masking</c> rules (transform), both keyed by the same
/// dot-separated property path. Produced by one walk of the schema in <see cref="SchemaRolesParser"/>.
/// </summary>
public sealed class SchemaExposureMetadata
{
    /// <summary>A schema that declares neither keyword.</summary>
    public static readonly SchemaExposureMetadata Empty = new(
        new Dictionary<string, IReadOnlyList<RoleGrant>>(0),
        new Dictionary<string, FieldMaskRule>(0));

    public SchemaExposureMetadata(
        IReadOnlyDictionary<string, IReadOnlyList<RoleGrant>> pathRoleGrants,
        IReadOnlyDictionary<string, FieldMaskRule> pathMaskRules)
    {
        PathRoleGrants = pathRoleGrants;
        PathMaskRules = pathMaskRules;
    }

    /// <summary>Property path → <c>x-roles</c> grants. Paths without grants are visible to all.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RoleGrant>> PathRoleGrants { get; }

    /// <summary>Property path → <c>x-masking</c> rule.</summary>
    public IReadOnlyDictionary<string, FieldMaskRule> PathMaskRules { get; }

    /// <summary>True when the schema neither hides nor masks anything.</summary>
    public bool IsEmpty => PathRoleGrants.Count == 0 && PathMaskRules.Count == 0;

    private IReadOnlySet<string>? _encryptPaths;

    private IReadOnlyDictionary<string, string>? _hashPaths;

    /// <summary>
    /// Paths declared <c>x-encryption.type: hash</c> → algorithm (<see cref="FieldMaskRule.Sha256"/> or
    /// <see cref="FieldMaskRule.Sha512"/>) — the ones the write funnel replaces with their digest.
    /// </summary>
    public IReadOnlyDictionary<string, string> HashPaths => _hashPaths ??= PathMaskRules
        .Where(r => r.Value.Operator == FieldMaskRule.HashOperator)
        .ToDictionary(r => r.Key, r => r.Value.HashAlgorithm, StringComparer.Ordinal);

    /// <summary>Paths declared <c>x-encryption.type: encrypt</c> — the ones the write funnel encrypts.</summary>
    public IReadOnlySet<string> EncryptPaths => _encryptPaths ??= PathMaskRules
        .Where(r => r.Value.IsAtRestEncryption)
        .Select(r => r.Key)
        .ToHashSet(StringComparer.Ordinal);
}
