using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Parses the field-exposure vocabulary of a JSON Schema (master schema): <c>x-roles</c> (who may see a
/// property), <c>x-masking</c> (how a visible property is masked on the way out) and <c>x-encryption</c> of
/// type <c>hash</c> (a salted one-way digest on the way out) or <c>encrypt</c> (AES-256-GCM at rest).
/// <para>
/// Both keywords are read in ONE walk over the same paths, so the hide and mask decisions can never
/// disagree about which property a path names. Parsing is lenient: shape errors are rejected at publish
/// time by <see cref="FieldMaskingDefinition"/>; here an unrecognizable <c>x-masking</c> still yields a
/// rule that masks, so a malformed declaration never turns into a clear value.
/// </para>
/// </summary>
public static class SchemaRolesParser
{
    private const string PropertiesKey = "properties";
    private const string RolesKey = "x-roles";
    internal const string MaskingKey = "x-masking";
    internal const string EncryptionKey = "x-encryption";

    /// <summary>
    /// Parses the schema and returns a map of property path to role grants.
    /// Path format: dot-separated (e.g. "amount", "internalNotes", "nested.field").
    /// Properties without "x-roles" are not included (treated as visible to all).
    /// </summary>
    /// <param name="schemaRoot">The root JsonElement of the schema (object with optional "properties").</param>
    /// <returns>Map of property path to list of role grants; empty if schema has no roles.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<RoleGrant>> ParsePropertyRoles(JsonElement schemaRoot)
        => ParseExposure(schemaRoot).PathRoleGrants;

    /// <summary>
    /// Parses both <c>x-roles</c> and <c>x-masking</c> in one walk. Only properties reachable through
    /// nested <c>properties</c> objects are considered — the same reachability the <c>x-roles</c> filter
    /// has always had, and the only one the publish-time validator admits for <c>x-masking</c>.
    /// </summary>
    public static SchemaExposureMetadata ParseExposure(JsonElement schemaRoot)
    {
        if (schemaRoot.ValueKind != JsonValueKind.Object)
            return SchemaExposureMetadata.Empty;

        var roles = new Dictionary<string, IReadOnlyList<RoleGrant>>(StringComparer.Ordinal);
        var masks = new Dictionary<string, FieldMaskRule>(StringComparer.Ordinal);
        ParseRecursive(schemaRoot, string.Empty, roles, masks);

        return roles.Count == 0 && masks.Count == 0
            ? SchemaExposureMetadata.Empty
            : new SchemaExposureMetadata(roles, masks);
    }

    private static void ParseRecursive(
        JsonElement node,
        string pathPrefix,
        Dictionary<string, IReadOnlyList<RoleGrant>> roles,
        Dictionary<string, FieldMaskRule> masks)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (!node.TryGetProperty(PropertiesKey, out var properties) || properties.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in properties.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(pathPrefix) ? property.Name : $"{pathPrefix}.{property.Name}";
            var propValue = property.Value;
            if (propValue.ValueKind != JsonValueKind.Object)
                continue;

            if (propValue.TryGetProperty(RolesKey, out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Array)
            {
                var grants = ParseRoleGrants(rolesElement);
                if (grants.Count > 0)
                    roles[path] = grants;
            }

            if (propValue.TryGetProperty(MaskingKey, out var maskingElement))
                masks[path] = ParseMaskRule(maskingElement);

            // x-encryption is the later stage, so on a property that carries both (a shape the publish
            // validator rejects) the encryption rule replaces the mask — either way the caller never sees
            // the value in clear unless exempt.
            if (propValue.TryGetProperty(EncryptionKey, out var encryptionElement) &&
                TryParseEncryptionRule(encryptionElement, out var encryptionRule))
                masks[path] = encryptionRule;

            if (propValue.TryGetProperty(PropertiesKey, out _))
                ParseRecursive(propValue, path, roles, masks);
        }
    }

    /// <summary>
    /// Lenient <c>x-masking</c> read. Anything that is not a recognizable <c>replace</c> becomes a full
    /// <c>mask</c>: a declaration the publish validator would have rejected must still fail closed if it
    /// ever reaches the runtime (a schema published by an older runtime, a bypassed validator).
    /// </summary>
    internal static FieldMaskRule ParseMaskRule(JsonElement masking)
    {
        if (masking.ValueKind != JsonValueKind.Object)
            return new FieldMaskRule(FieldMaskRule.MaskOperator, FieldMaskRule.DefaultMaskingChar, 0, 0, null, []);

        var op = ReadString(masking, "operator")?.Trim().ToLowerInvariant();
        masking.TryGetProperty("params", out var parameters);
        var exempt = masking.TryGetProperty("roles", out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Array
            ? ParseExemptGrants(rolesElement)
            : [];

        if (op == FieldMaskRule.ReplaceOperator)
        {
            var value = parameters.ValueKind == JsonValueKind.Object ? ReadString(parameters, "value") : null;
            return new FieldMaskRule(FieldMaskRule.ReplaceOperator, FieldMaskRule.DefaultMaskingChar, 0, 0, value ?? string.Empty, exempt);
        }

        var maskingChar = FieldMaskRule.DefaultMaskingChar;
        int keepFirst = 0, keepLast = 0;
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            var ch = ReadString(parameters, "maskingChar");
            if (!string.IsNullOrEmpty(ch) && ch.Length == 1)
                maskingChar = ch;
            keepFirst = ReadNonNegativeInt(parameters, "keepFirst");
            keepLast = ReadNonNegativeInt(parameters, "keepLast");
        }

        return new FieldMaskRule(FieldMaskRule.MaskOperator, maskingChar, keepFirst, keepLast, null, exempt);
    }

    /// <summary>
    /// Reads an <c>x-encryption</c> declaration of <c>type: "hash"</c> or <c>"encrypt"</c>. Any other type (or shape)
    /// yields no rule. Case is tolerated here and rejected at publish (<see cref="FieldMaskingDefinition"/>): a
    /// declaration that slipped past the validator must still fail closed rather than serve the value in clear.
    /// </summary>
    internal static bool TryParseEncryptionRule(JsonElement encryption, out FieldMaskRule rule)
    {
        rule = null!;
        if (encryption.ValueKind != JsonValueKind.Object)
            return false;

        var type = ReadString(encryption, "type")?.Trim();
        var exemptGrants = encryption.TryGetProperty("roles", out var exemptElement) && exemptElement.ValueKind == JsonValueKind.Array
            ? ParseExemptGrants(exemptElement)
            : [];

        if (string.Equals(type, FieldMaskRule.EncryptOperator, StringComparison.OrdinalIgnoreCase))
        {
            rule = new FieldMaskRule(FieldMaskRule.EncryptOperator, FieldMaskRule.DefaultMaskingChar, 0, 0, null, exemptGrants);
            return true;
        }

        if (!string.Equals(type, FieldMaskRule.HashOperator, StringComparison.OrdinalIgnoreCase))
            return false;

        var algorithm = FieldMaskRule.Sha256;
        if (encryption.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
            string.Equals(ReadString(parameters, "algorithm")?.Trim(), FieldMaskRule.Sha512, StringComparison.OrdinalIgnoreCase))
            algorithm = FieldMaskRule.Sha512;

        // Hashing is applied on write and is irreversible: there is no raw value to show anyone, so a hash rule
        // carries no exemption list whatever the declaration says (the publish validator rejects roles on it).
        rule = new FieldMaskRule(FieldMaskRule.HashOperator, FieldMaskRule.DefaultMaskingChar, 0, 0, null, [], algorithm);
        return true;
    }

    /// <summary>
    /// Exemption grants are allow-only: a caller matching one sees the raw value, everyone else the transformed one.
    /// A <c>deny</c> entry is dropped rather than honoured (the publish validator rejects it), so a stray deny can never
    /// widen what anyone sees. A combinator is dropped too (decision K1, the validator rejects it): no exemption means
    /// the value stays masked — fail closed.
    /// </summary>
    private static IReadOnlyList<RoleGrant> ParseExemptGrants(JsonElement rolesArray)
    {
        var all = ParseRoleGrants(rolesArray);
        if (all.Count == 0)
            return all;

        var allow = new List<RoleGrant>(all.Count);
        foreach (var grant in all)
        {
            if (!grant.IsDeny && !grant.IsCombinator)
                allow.Add(grant);
        }

        return allow;
    }

    /// <summary>
    /// Reads every entry as a <see cref="RoleGrant"/> (a plain role, or an <c>allOf</c> / <c>anyOf</c> combinator).
    /// Lenient at runtime: an entry that does not parse is skipped, never thrown on — the publish-time validator
    /// (<c>SchemaComponentValidator</c>) is the gate that rejects it with the reason.
    /// </summary>
    private static IReadOnlyList<RoleGrant> ParseRoleGrants(JsonElement rolesArray)
    {
        var list = new List<RoleGrant>();
        foreach (var item in rolesArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            try
            {
                var grant = item.Deserialize<RoleGrant>(JsonSerializerConstants.JsonOptions);
                if (grant is not null)
                    list.Add(TrimRoles(grant));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // Skip an unparseable grant (unknown grant type, bad shape, extra child member).
            }
        }
        return list;
    }

    /// <summary>
    /// x-roles role names have always been trimmed (the pre-combinator parser did <c>role.Trim()</c>); the grant types
    /// keep what they are given, so the trim happens here. Without it a padded deny would stop matching its role.
    /// </summary>
    private static RoleGrant TrimRoles(RoleGrant grant)
    {
        static bool Padded(string role) => role.Length != role.Trim().Length;

        if (grant.Role is not null)
            return Padded(grant.Role) ? new RoleGrant(grant.Role.Trim(), grant.Grant) : grant;

        static IReadOnlyList<RoleGrantCondition>? TrimChildren(IReadOnlyList<RoleGrantCondition>? children)
            => children?.Any(c => Padded(c.Role)) == true
                ? children.Select(c => new RoleGrantCondition(c.Role.Trim())).ToList()
                : children;

        var allOf = TrimChildren(grant.AllOf);
        var anyOf = TrimChildren(grant.AnyOf);
        return ReferenceEquals(allOf, grant.AllOf) && ReferenceEquals(anyOf, grant.AnyOf)
            ? grant
            : new RoleGrant(null, grant.Grant, allOf, anyOf);
    }

    /// <summary>
    /// Every role array a schema declares, with the path of its property: <c>x-roles</c> (a full grant list) and the
    /// <c>roles</c> of <c>x-masking</c> / <c>x-encryption</c> (exemption lists, which take no combinators). Same walk
    /// as the parser, so the publish-time validator and the runtime see the same arrays.
    /// </summary>
    public static IEnumerable<(string Path, string Member, JsonElement Array, bool Exemption)> EnumerateRoleArrays(
        JsonElement schemaRoot)
    {
        var found = new List<(string, string, JsonElement, bool)>();
        CollectRoleArrays(schemaRoot, string.Empty, found);
        return found;
    }

    private static void CollectRoleArrays(
        JsonElement node, string pathPrefix, List<(string, string, JsonElement, bool)> found)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty(PropertiesKey, out var properties) || properties.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in properties.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(pathPrefix) ? property.Name : $"{pathPrefix}.{property.Name}";
            var propValue = property.Value;
            if (propValue.ValueKind != JsonValueKind.Object)
                continue;

            if (propValue.TryGetProperty(RolesKey, out var xRoles) && xRoles.ValueKind == JsonValueKind.Array)
                found.Add((path, $"schema.{RolesKey}", xRoles, false));

            foreach (var keyword in new[] { MaskingKey, EncryptionKey })
            {
                if (propValue.TryGetProperty(keyword, out var declaration) && declaration.ValueKind == JsonValueKind.Object &&
                    declaration.TryGetProperty("roles", out var exempt) && exempt.ValueKind == JsonValueKind.Array)
                    found.Add((path, $"schema.{keyword}.roles", exempt, true));
            }

            if (propValue.TryGetProperty(PropertiesKey, out _))
                CollectRoleArrays(propValue, path, found);
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadNonNegativeInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number) && number > 0
            ? number
            : 0;
}
