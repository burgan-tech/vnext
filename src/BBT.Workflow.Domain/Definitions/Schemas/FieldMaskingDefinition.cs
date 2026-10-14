using System.Text.Json;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>A publish-time violation, tagged with the keyword it belongs to (<c>x-masking</c> or <c>x-encryption</c>).</summary>
/// <param name="Keyword">The vocabulary keyword the error is reported under.</param>
/// <param name="Message">Human-readable message naming the property path.</param>
public sealed record FieldExposureError(string Keyword, string Message);

/// <summary>
/// Publish-time rules for the field-exposure transforms — <c>x-masking</c> and <c>x-encryption</c>.
/// Everything the runtime relies on when it transforms a value is enforced here, once, so the read path can
/// stay lenient (see <see cref="SchemaRolesParser"/>).
/// <para><b>Shared by <c>x-masking</c> and <c>x-encryption</c> of type <c>hash</c> / <c>encrypt</c></b>:</para>
/// <list type="bullet">
/// <item>Any schema component type. The transforms take effect only where the schema is a workflow's data
/// (master) schema — the one <c>workflow.schema</c> references — because that is the schema the read path
/// resolves; elsewhere (a transition input schema) the declaration is inert. The component's
/// <c>attributes.type</c> is deliberately NOT checked: domains publish their data schemas as <c>workflow</c>,
/// <c>schema</c> or <c>master</c>, and gating on one spelling (as <c>x-indexed</c> does) would reject them.</item>
/// <item>Only on a property reachable through nested <c>properties</c> — the same reachability the
/// <c>x-roles</c> filter has. Under <c>items</c>, <c>$defs</c>, combinators or conditionals it would be
/// silently ignored, so it is rejected instead.</item>
/// <item>Only on <c>type: "string"</c> (optionally with <c>"null"</c>): the output is a string.</item>
/// <item>Not together with <c>x-filterOperators</c>, <c>x-sortable</c> or <c>x-indexed</c> — list filters,
/// sorting and attribute indexes run on the stored value in SQL, so the field would stay probeable through
/// them (and an encrypted field holds ciphertext there).</item>
/// <item><c>roles</c> is an allow-only exemption list: a matching caller sees the raw value, everyone else — a typo,
/// a role added later in the identity provider, no roles at all — sees it transformed (fail closed). A <c>deny</c>
/// entry is rejected. <c>hash</c> takes no <c>roles</c>: it is applied on write and cannot be undone.</item>
/// </list>
/// <para><c>x-masking</c> additionally: <c>operator</c> is <c>mask</c> or <c>replace</c> with that operator's
/// parameters only, and it may not sit next to an active <c>x-encryption</c> (type other than <c>none</c>).</para>
/// <para><c>x-encryption</c>: an object whose <c>type</c> is exactly <c>none</c>, <c>hash</c> or <c>encrypt</c>
/// (lower case — the read path tolerates case, publish does not). <c>persisted</c> and <c>transport</c> were
/// never enforced and are removed in favour of <c>encrypt</c> (see vnext-meta <c>deprecations.json</c>).
/// <c>hash</c> takes <c>params.algorithm</c> only; <c>encrypt</c> takes no params (algorithm fixed, key from host
/// configuration). Salts and keys are never schema content. Whether the host can actually encrypt (an active
/// key) is checked by the application-layer validator.</para>
/// <para><c>x-encryption.source</c> says WHO encrypts: <c>runtime</c> (the default when absent) is the at-rest rule
/// above; <c>client</c> is an in-transit declaration the client honours — it seals the value to the recipient's public
/// key before the request leaves. The runtime does nothing with a <c>client</c> rule (no write-time encryption, no read
/// transform, no filter refusal) on any schema, the master included; here it only checks the members (<c>key</c> and
/// <c>algorithm</c> required, the at-rest members refused).</para>
/// </summary>
public static class FieldMaskingDefinition
{
    private const string Masking = SchemaRolesParser.MaskingKey;
    private const string Encryption = SchemaRolesParser.EncryptionKey;
    private static readonly HashSet<string> MaskParams = new(StringComparer.Ordinal) { "maskingChar", "keepFirst", "keepLast" };
    private static readonly HashSet<string> ReplaceParams = new(StringComparer.Ordinal) { "value" };
    private static readonly HashSet<string> MaskingKeys = new(StringComparer.Ordinal) { "operator", "params", "roles" };
    private static readonly HashSet<string> EncryptionKeys = new(StringComparer.Ordinal)
        { SourceKey, "type", "params", "roles", "purpose", "redactInLogs", "retentionDays" };
    private static readonly HashSet<string> ClientEncryptionKeys = new(StringComparer.Ordinal)
        { SourceKey, "key", "algorithm", "into", "encoding", "nonce", "envelope", "purpose" };
    private static readonly HashSet<string> ClientAlgorithms = new(StringComparer.Ordinal)
        { "HPKE-P256-SHA256-AES128GCM", "RSA-OAEP-256" };
    private static readonly HashSet<string> ClientEncodings = new(StringComparer.Ordinal) { "base64", "base64url" };
    private const string SourceKey = "source";

    /// <summary><c>x-encryption.source</c> of the at-rest rule the runtime applies (the default when absent).</summary>
    public const string RuntimeSource = "runtime";

    /// <summary><c>x-encryption.source</c> of an in-transit rule the client applies; the runtime ignores it.</summary>
    public const string ClientSource = "client";
    private const string NoneType = "none";
    private static readonly string[] HashIncompatibleKeywords =
        ["pattern", "format", "minLength", "maxLength", "enum", "const"];

    /// <summary>Validates every <c>x-masking</c> and <c>x-encryption</c> declaration in <paramref name="root"/>.</summary>
    /// <param name="root">The schema document (<c>attributes.schema</c>).</param>
    /// <param name="schemaType">The component's <c>attributes.type</c>. Not used for gating (see the type summary); kept so callers do not change.</param>
    /// <returns>One error per violation, each naming the property path; empty when valid.</returns>
    public static IReadOnlyList<FieldExposureError> Validate(JsonElement root, string? schemaType)
    {
        var errors = new List<FieldExposureError>();
        if (root.ValueKind != JsonValueKind.Object)
            return errors;

        Visit(root, string.Empty, reachable: true, errors);
        return errors;
    }

    /// <summary>
    /// True when the <c>x-encryption</c> declaration is a client-sourced (in-transit) rule. Exact, case-sensitive match:
    /// any other spelling stays a runtime rule, so a misspelled source fails closed into the at-rest path.
    /// </summary>
    public static bool IsClientSourced(JsonElement encryption)
        => encryption.ValueKind == JsonValueKind.Object &&
           encryption.TryGetProperty(SourceKey, out var source) &&
           source.ValueKind == JsonValueKind.String &&
           source.GetString() == ClientSource;

    private static void Visit(JsonElement node, string path, bool reachable, List<FieldExposureError> errors)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
                Visit(child, path, reachable: false, errors);
            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (node.TryGetProperty(Masking, out var masking))
            ValidateMasking(node, masking, path, reachable, errors);

        if (node.TryGetProperty(Encryption, out var encryption))
        {
            if (IsClientSourced(encryption))
                ValidateClientEncryption(encryption, path, errors);
            else
                ValidateEncryption(node, encryption, path, reachable, errors);
        }

        foreach (var property in node.EnumerateObject())
        {
            if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in property.Value.EnumerateObject())
                {
                    var childPath = path.Length == 0 ? child.Name : $"{path}.{child.Name}";
                    Visit(child.Value, childPath, reachable, errors);
                }
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                     !property.Name.StartsWith("x-", StringComparison.Ordinal) &&
                     property.Name is not ("default" or "examples" or "enum" or "const"))
            {
                // items, prefixItems, $defs, definitions, combinators, conditionals, patternProperties, …:
                // the read path never resolves a path through them.
                if (property.Name is "$defs" or "definitions" or "patternProperties" or "dependentSchemas" &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var child in property.Value.EnumerateObject())
                        Visit(child.Value, path, reachable: false, errors);
                }
                else
                {
                    Visit(property.Value, path, reachable: false, errors);
                }
            }
        }
    }

    // ── x-masking ────────────────────────────────────────────────────────────

    private static void ValidateMasking(
        JsonElement property, JsonElement masking, string path, bool reachable, List<FieldExposureError> errors)
    {
        var where = Where(path);
        void Error(string message) => errors.Add(new FieldExposureError(Masking, $"Field '{where}': {message}"));

        if (!ValidatePlacement(property, path, reachable, Masking, Error))
            return;

        if (property.TryGetProperty(Encryption, out var encryption) && IsActiveEncryption(encryption))
            Error("x-masking cannot be combined with an active x-encryption (type other than 'none'); declare one transform per field.");

        if (masking.ValueKind != JsonValueKind.Object)
        {
            Error("x-masking must be an object.");
            return;
        }

        foreach (var key in masking.EnumerateObject())
        {
            if (!MaskingKeys.Contains(key.Name))
                Error($"x-masking does not support '{key.Name}'. Allowed: operator, params, roles.");
        }

        var op = masking.TryGetProperty("operator", out var opElement) && opElement.ValueKind == JsonValueKind.String
            ? opElement.GetString()
            : null;

        switch (op)
        {
            case FieldMaskRule.MaskOperator:
                ValidateMaskingParams(masking, MaskParams, Error, requireValue: false);
                break;
            case FieldMaskRule.ReplaceOperator:
                ValidateMaskingParams(masking, ReplaceParams, Error, requireValue: true);
                break;
            default:
                Error("x-masking.operator must be 'mask' or 'replace'.");
                break;
        }

        ValidateExemptRoles(masking, Masking, Error);
    }

    private static void ValidateMaskingParams(
        JsonElement masking, HashSet<string> allowed, Action<string> error, bool requireValue)
    {
        if (!masking.TryGetProperty("params", out var parameters))
        {
            if (requireValue)
                error("x-masking operator 'replace' requires params.value.");
            return;
        }

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            error("x-masking.params must be an object.");
            return;
        }

        foreach (var param in parameters.EnumerateObject())
        {
            if (!allowed.Contains(param.Name))
            {
                error($"x-masking.params.{param.Name} is not a parameter of this operator.");
                continue;
            }

            switch (param.Name)
            {
                case "maskingChar":
                    if (param.Value.ValueKind != JsonValueKind.String || param.Value.GetString()!.Length != 1)
                        error("x-masking.params.maskingChar must be a single character.");
                    break;
                case "keepFirst" or "keepLast":
                    if (param.Value.ValueKind != JsonValueKind.Number || !param.Value.TryGetInt32(out var n) || n < 0)
                        error($"x-masking.params.{param.Name} must be a non-negative integer.");
                    break;
                case "value":
                    if (param.Value.ValueKind != JsonValueKind.String)
                        error("x-masking.params.value must be a string.");
                    break;
            }
        }

        if (requireValue && !parameters.TryGetProperty("value", out _))
            error("x-masking operator 'replace' requires params.value.");
    }

    // ── x-encryption ─────────────────────────────────────────────────────────

    private static void ValidateEncryption(
        JsonElement property, JsonElement encryption, string path, bool reachable, List<FieldExposureError> errors)
    {
        var where = Where(path);
        void Error(string message) => errors.Add(new FieldExposureError(Encryption, $"Field '{where}': {message}"));

        if (encryption.ValueKind != JsonValueKind.Object)
        {
            Error("x-encryption must be an object with a 'type'.");
            return;
        }

        foreach (var key in encryption.EnumerateObject())
        {
            if (!EncryptionKeys.Contains(key.Name))
                Error(ClientEncryptionKeys.Contains(key.Name)
                    ? $"x-encryption.{key.Name} belongs to source 'client'; a runtime (at-rest) rule does not accept it."
                    : $"x-encryption does not support '{key.Name}'. Allowed: source, type, params, roles, purpose, redactInLogs, retentionDays.");
        }

        if (encryption.TryGetProperty(SourceKey, out var source) &&
            (source.ValueKind != JsonValueKind.String || source.GetString() != RuntimeSource))
            Error($"x-encryption.source must be '{RuntimeSource}' or '{ClientSource}'.");

        var type = encryption.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        switch (type)
        {
            case NoneType:
                if (encryption.TryGetProperty("params", out _))
                    Error("x-encryption.params is not supported for type 'none'.");
                break;

            case FieldMaskRule.HashOperator:
                if (ValidatePlacement(property, path, reachable, Encryption, Error))
                {
                    ValidateHashParams(encryption, Error);

                    // Hashing happens on write and cannot be undone: nobody can be shown the raw value.
                    if (encryption.TryGetProperty("roles", out _))
                        Error("x-encryption.roles is not supported for type 'hash'; the value is hashed when it is written and the raw value no longer exists.");

                    // Every later write re-validates the merged document, which then holds the digest — a
                    // constraint written for the raw value would reject it and the instance could no longer be written.
                    foreach (var keyword in HashIncompatibleKeywords)
                    {
                        if (property.TryGetProperty(keyword, out _))
                            Error($"x-encryption type 'hash' cannot be combined with '{keyword}'; the stored value is the digest, which the constraint would reject on the next write.");
                    }
                }
                break;

            case FieldMaskRule.EncryptOperator:
                if (ValidatePlacement(property, path, reachable, Encryption, Error) &&
                    encryption.TryGetProperty("params", out _))
                    Error("x-encryption.params is not supported for type 'encrypt'; the algorithm is fixed (AES-256-GCM) and the key is generated per instance by the runtime, never taken from a schema.");
                break;

            case "persisted" or "transport":
                Error($"x-encryption.type '{type}' has been removed; use 'encrypt' (AES-256-GCM at rest, decrypted on read for the exempt roles).");
                break;

            case null:
                Error("x-encryption.type is required: 'none', 'hash' or 'encrypt'.");
                break;

            default:
                Error(string.Equals(type, FieldMaskRule.HashOperator, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(type, FieldMaskRule.EncryptOperator, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(type, NoneType, StringComparison.OrdinalIgnoreCase)
                    ? $"x-encryption.type must be written in lower case ('{type!.ToLowerInvariant()}')."
                    : $"x-encryption.type '{type}' is not supported. Allowed: none, hash, encrypt.");
                break;
        }

        // Fields added with this release: validated on every type, because no existing schema can carry them
        // (hash rejects roles outright above).
        if (type != FieldMaskRule.HashOperator)
            ValidateExemptRoles(encryption, Encryption, Error);

        if (encryption.TryGetProperty("purpose", out var purpose) &&
            (purpose.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(purpose.GetString())))
            Error("x-encryption.purpose must be a non-empty string.");

        if (encryption.TryGetProperty("redactInLogs", out var redact) &&
            redact.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            Error("x-encryption.redactInLogs must be a boolean.");

        if (encryption.TryGetProperty("retentionDays", out var retention) &&
            (retention.ValueKind != JsonValueKind.Number || !retention.TryGetInt32(out var days) || days < 1))
            Error("x-encryption.retentionDays must be a positive integer.");
    }

    /// <summary>
    /// A client-sourced rule, on any schema: the client seals the value, the runtime never touches it. Only the members are checked —
    /// the suite and key address are the client's contract (the vocabulary describes them).
    /// </summary>
    private static void ValidateClientEncryption(
        JsonElement encryption, string path, List<FieldExposureError> errors)
    {
        var where = Where(path);
        void Error(string message) => errors.Add(new FieldExposureError(Encryption, $"Field '{where}': {message}"));

        foreach (var key in encryption.EnumerateObject())
        {
            if (!ClientEncryptionKeys.Contains(key.Name))
                Error(EncryptionKeys.Contains(key.Name)
                    ? $"x-encryption.{key.Name} belongs to source 'runtime' (at rest); a client rule does not accept it."
                    : $"x-encryption does not support '{key.Name}'. Allowed for source 'client': key, algorithm, into, encoding, nonce, envelope, purpose.");
        }

        if (!encryption.TryGetProperty("key", out var keyElement) ||
            keyElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(keyElement.GetString()))
            Error("x-encryption.key is required for source 'client': the address of the recipient's public key.");

        if (!encryption.TryGetProperty("algorithm", out var algorithm) ||
            algorithm.ValueKind != JsonValueKind.String || !ClientAlgorithms.Contains(algorithm.GetString()!))
            Error("x-encryption.algorithm is required for source 'client': 'HPKE-P256-SHA256-AES128GCM' or 'RSA-OAEP-256'.");

        foreach (var name in new[] { "into", "nonce" })
        {
            if (encryption.TryGetProperty(name, out var value) &&
                (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                Error($"x-encryption.{name} must be a non-empty string.");
        }

        if (encryption.TryGetProperty("encoding", out var encoding) &&
            (encoding.ValueKind != JsonValueKind.String || !ClientEncodings.Contains(encoding.GetString()!)))
            Error("x-encryption.encoding must be 'base64' or 'base64url'.");

        if (encryption.TryGetProperty("envelope", out var envelope) &&
            (envelope.ValueKind != JsonValueKind.String || envelope.GetString() != "publicKey"))
            Error("x-encryption.envelope must be 'publicKey'.");

        if (encryption.TryGetProperty("purpose", out var purpose) &&
            (purpose.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(purpose.GetString())))
            Error("x-encryption.purpose must be a non-empty string.");
    }

    private static void ValidateHashParams(JsonElement encryption, Action<string> error)
    {
        if (!encryption.TryGetProperty("params", out var parameters))
            return;

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            error("x-encryption.params must be an object.");
            return;
        }

        foreach (var param in parameters.EnumerateObject())
        {
            if (param.Name != "algorithm")
            {
                // A salt or key in a schema would be served verbatim by the master function.
                error($"x-encryption.params.{param.Name} is not supported; the hash salt is generated per instance by the runtime, never taken from a schema.");
                continue;
            }

            if (param.Value.ValueKind != JsonValueKind.String ||
                param.Value.GetString() is not (FieldMaskRule.Sha256 or FieldMaskRule.Sha512))
                error("x-encryption.params.algorithm must be 'sha256' or 'sha512'.");
        }
    }

    // ── shared ───────────────────────────────────────────────────────────────

    private static bool ValidatePlacement(
        JsonElement property, string path, bool reachable, string keyword, Action<string> error)
    {
        if (!reachable || path.Length == 0)
        {
            error($"{keyword} is only supported on properties reachable through nested 'properties'; " +
                  "it is not applied under items, $defs, combinators or conditional schemas.");
            return false;
        }

        if (!IsStringType(property))
            error($"{keyword} requires the property to declare type \"string\" (optionally with \"null\").");

        if (property.TryGetProperty("x-filterOperators", out var ops) &&
            ops.ValueKind == JsonValueKind.Array && ops.GetArrayLength() > 0)
            error($"{keyword} cannot be combined with x-filterOperators; list filters run on the raw value and would expose it.");

        if (property.TryGetProperty("x-sortable", out var sortable) && sortable.ValueKind == JsonValueKind.True)
            error($"{keyword} cannot be combined with x-sortable; sorting runs on the raw value and would expose its order.");

        if (property.TryGetProperty("x-indexed", out var indexed) && indexed.ValueKind == JsonValueKind.True)
            error($"{keyword} cannot be combined with x-indexed; the attribute index is computed from the stored value in the database.");

        return true;
    }

    private static void ValidateExemptRoles(JsonElement declaration, string keyword, Action<string> error)
    {
        if (!declaration.TryGetProperty("roles", out var roles))
            return;

        if (roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() == 0)
        {
            error($"{keyword}.roles must be a non-empty array of grants.");
            return;
        }

        foreach (var item in roles.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(role.GetString()) ||
                !item.TryGetProperty("grant", out var grant) || grant.ValueKind != JsonValueKind.String)
            {
                error($"every {keyword}.roles entry needs a non-empty 'role' and a 'grant'.");
                continue;
            }

            var roleName = role.GetString()!.Trim();
            if (!string.Equals(grant.GetString()?.Trim(), GrantKind.Allow, StringComparison.OrdinalIgnoreCase))
            {
                error($"{keyword}.roles is an exemption list; only grant 'allow' is accepted (role '{roleName}'). " +
                      "A matching caller sees the raw value, everyone else sees it transformed.");
                continue;
            }

            var message = DynamicRoleGrant.Classify(roleName) switch
            {
                DynamicRoleFormat.MissingContextPrefix =>
                    $"dynamic role '{roleName}' in {keyword}.roles must start with '$.context.' (case-sensitive).",
                DynamicRoleFormat.EmptyNavigationPath =>
                    $"dynamic role '{roleName}' in {keyword}.roles has an empty navigation path.",
                _ => null
            };
            if (message != null)
                error(message);
        }
    }

    private static string Where(string path) => path.Length == 0 ? "(root)" : path;

    private static bool IsStringType(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
            return false;

        if (type.ValueKind == JsonValueKind.String)
            return type.GetString() == "string";

        if (type.ValueKind != JsonValueKind.Array)
            return false;

        var hasString = false;
        foreach (var t in type.EnumerateArray())
        {
            var name = t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (name == "string")
                hasString = true;
            else if (name != "null")
                return false;
        }

        return hasString;
    }

    /// <summary>An at-rest rule other than <c>none</c>. A client-sourced rule is never active: the runtime does not apply it.</summary>
    internal static bool IsActiveEncryption(JsonElement encryption)
        => !IsClientSourced(encryption) && (encryption.ValueKind != JsonValueKind.Object ||
           !encryption.TryGetProperty("type", out var type) ||
           type.ValueKind != JsonValueKind.String ||
           !string.Equals(type.GetString(), NoneType, StringComparison.OrdinalIgnoreCase));
}
