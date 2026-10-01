using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Writes the caller-facing form of instance data in one pass: properties the caller may not see
/// (<c>x-roles</c>) are removed, and visible properties carrying an active <c>x-masking</c> rule are
/// transformed. Paths without either keyword are copied verbatim.
/// <para>
/// Order is structural, not procedural: a pruned property is skipped before anything looks at its value,
/// so a hidden field can never reach a mask rule, and its children are never visited.
/// </para>
/// </summary>
public static class InstanceDataRoleFilter
{
    private static readonly IReadOnlyDictionary<string, FieldMaskRule> NoMaskRules =
        new Dictionary<string, FieldMaskRule>(0);

    /// <summary>
    /// Filters the instance data to only include properties visible to the caller.
    /// </summary>
    /// <param name="data">Root JsonElement (object or array).</param>
    /// <param name="pathsWithRoles">Set of property paths that have role restrictions in the schema (keys from SchemaRolesParser).</param>
    /// <param name="visiblePaths">Set of paths that the caller is allowed to see (from SchemaFieldVisibilityService).</param>
    /// <returns>A new JsonElement containing only visible properties; or the original if nothing to filter.</returns>
    public static JsonElement FilterByVisiblePaths(
        JsonElement data,
        IReadOnlySet<string> pathsWithRoles,
        IReadOnlySet<string> visiblePaths)
        => Apply(data, pathsWithRoles, visiblePaths, NoMaskRules, maskingEngine: null);

    /// <summary>
    /// Prunes hidden paths and masks the visible paths in <paramref name="activeMaskRules"/>.
    /// </summary>
    /// <param name="data">Root JsonElement.</param>
    /// <param name="pathsWithRoles">Paths guarded by <c>x-roles</c>.</param>
    /// <param name="visiblePaths">Guarded paths the caller may see.</param>
    /// <param name="activeMaskRules">
    /// Mask rules that apply to THIS caller (exemptions already resolved). A path that is also hidden is
    /// pruned, never masked.
    /// </param>
    /// <param name="maskingEngine">Required when <paramref name="activeMaskRules"/> is non-empty.</param>
    /// <param name="storedTokens">
    /// Stored <c>x-encryption.type: "encrypt"</c> token per path of the row <paramref name="data"/> was read from
    /// (<see cref="InstanceDataView.Tokens"/> of the row opened by <see cref="IInstanceDataProtector"/>). An active encrypt rule serves the token; without one — a legacy
    /// plaintext value written before the field was encrypted — the value is fully masked, never shown.
    /// </param>
    public static JsonElement Apply(
        JsonElement data,
        IReadOnlySet<string> pathsWithRoles,
        IReadOnlySet<string> visiblePaths,
        IReadOnlyDictionary<string, FieldMaskRule> activeMaskRules,
        IFieldMaskingEngine? maskingEngine,
        IReadOnlyDictionary<string, string>? storedTokens = null)
    {
        if (pathsWithRoles.Count == 0 && activeMaskRules.Count == 0)
            return data;

        if (activeMaskRules.Count > 0 && maskingEngine is null)
            throw new ArgumentNullException(nameof(maskingEngine), "Mask rules are active but no masking engine was supplied.");

        if (data.ValueKind == JsonValueKind.Object)
            return FilterObject(data, string.Empty, new WalkContext(
                pathsWithRoles, visiblePaths, activeMaskRules, maskingEngine, storedTokens ?? NoTokens));

        return data;
    }

    private static readonly IReadOnlyDictionary<string, string> NoTokens = new Dictionary<string, string>(0);

    private sealed record WalkContext(
        IReadOnlySet<string> PathsWithRoles,
        IReadOnlySet<string> VisiblePaths,
        IReadOnlyDictionary<string, FieldMaskRule> MaskRules,
        IFieldMaskingEngine? Engine,
        IReadOnlyDictionary<string, string> StoredTokens);

    private static JsonElement FilterObject(JsonElement obj, string parentPath, WalkContext ctx)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var property in obj.EnumerateObject())
            {
                var path = string.IsNullOrEmpty(parentPath) ? property.Name : $"{parentPath}.{property.Name}";
                if (!IsPathVisible(path, ctx.PathsWithRoles, ctx.VisiblePaths))
                    continue;

                writer.WritePropertyName(property.Name);
                WriteFilteredValue(property.Value, path, ctx, writer);
            }
            writer.WriteEndObject();
        }
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    private static void WriteFilteredValue(
        JsonElement value,
        string currentPath,
        WalkContext ctx,
        Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var filtered = FilterObject(value, currentPath, ctx);
                filtered.WriteTo(writer);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                for (var i = 0; i < value.GetArrayLength(); i++)
                {
                    var item = value[i];
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var filteredObj = FilterObject(item, currentPath, ctx);
                        filteredObj.WriteTo(writer);
                    }
                    else
                    {
                        WriteLeaf(item, currentPath, ctx, writer);
                    }
                }
                writer.WriteEndArray();
                break;
            default:
                WriteLeaf(value, currentPath, ctx, writer);
                break;
        }
    }

    /// <summary>
    /// Writes a scalar. A string under an active rule is masked; a number or boolean under an active rule
    /// (a value that contradicts its own <c>type: string</c> declaration — the publish validator admits
    /// nothing else) is masked as its raw text rather than leaked; null stays null.
    /// </summary>
    private static void WriteLeaf(JsonElement value, string path, WalkContext ctx, Utf8JsonWriter writer)
    {
        if (ctx.MaskRules.Count == 0 || !ctx.MaskRules.TryGetValue(path, out var rule))
        {
            value.WriteTo(writer);
            return;
        }

        if (rule.IsAtRestEncryption)
        {
            WriteEncryptedLeaf(value, path, rule, ctx, writer);
            return;
        }

        // x-encryption "hash" is applied on write: the stored digest is served as it is. A value without the prefix
        // was written before the rule existed and is still the raw value — nobody is shown it.
        if (rule.Operator == FieldMaskRule.HashOperator)
        {
            if (value.ValueKind == JsonValueKind.Null ||
                (value.ValueKind == JsonValueKind.String && EncryptedValueFormat.IsHashed(value.GetString())))
                value.WriteTo(writer);
            else
                writer.WriteStringValue(ctx.Engine!.Apply(rule, value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : value.GetRawText()));
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(ctx.Engine!.Apply(rule, value.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                writer.WriteStringValue(ctx.Engine!.Apply(rule, value.GetRawText()));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// A caller outside the exemption list: the stored token for a value that was encrypted; the value itself when
    /// it already is a token (one this host could not open); a full mask for a legacy plaintext value; null stays null.
    /// </summary>
    private static void WriteEncryptedLeaf(JsonElement value, string path, FieldMaskRule rule, WalkContext ctx, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            value.WriteTo(writer);
            return;
        }

        if (ctx.StoredTokens.TryGetValue(path, out var token))
        {
            writer.WriteStringValue(token);
            return;
        }

        if (value.ValueKind == JsonValueKind.String && EncryptedValueFormat.IsToken(value.GetString()))
        {
            value.WriteTo(writer);
            return;
        }

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        writer.WriteStringValue(ctx.Engine!.Apply(rule, raw));
    }

    private static bool IsPathVisible(string path, IReadOnlySet<string> pathsWithRoles, IReadOnlySet<string> visiblePaths)
    {
        if (!pathsWithRoles.Contains(path))
            return true;
        return visiblePaths.Contains(path);
    }
}
