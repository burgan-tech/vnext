using System.Text.Json;
using BBT.Workflow.Security;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// A single authoring problem found in the <c>x-sensitive</c> annotations of one schema.
/// <para>
/// Every problem is fatal to publishing, deliberately. There is no warning tier because each
/// case this parser detects is either a contradiction or — worse — an annotation that reads as
/// protection and delivers none; a warning on a security marker is a warning nobody reads.
/// <c>x-sensitive</c> is a new vocabulary, so no existing definition can be broken by being
/// strict from the start.
/// </para>
/// </summary>
/// <param name="Path">Schema location the problem was found at.</param>
/// <param name="Message">Author-facing explanation.</param>
public sealed record SensitiveSchemaProblem(string Path, string Message);

/// <summary>
/// Reads the <c>x-sensitive</c> vocabulary out of a JSON Schema.
/// <para>
/// Two entry points with deliberately different tempers: <see cref="Parse"/> is lenient and is
/// what the runtime uses (a malformed annotation is skipped rather than failing a live
/// transition), and <see cref="Validate"/> is strict and is what component publishing uses, so
/// the mistake surfaces at definition time where the author can see it.
/// </para>
/// </summary>
public static class SensitiveSchemaParser
{
    /// <summary>The vocabulary keyword. Must match the <c>vnext-schema</c> spelling exactly.</summary>
    public const string SensitiveKey = "x-sensitive";

    private const string TypeKey = "type";
    private const string FilterOperatorsKey = "x-filterOperators";
    private const string SortableKey = "x-sortable";

    private const string EnabledKey = "enabled";
    private const string PurposeKey = "purpose";
    private const string EncryptAtRestKey = "encryptAtRest";
    private const string RedactInLogsKey = "redactInLogs";
    private const string MaskingPatternKey = "maskingPattern";
    private const string RetentionDaysKey = "retentionDays";

    /// <summary>
    /// Parses every reachable <c>x-sensitive</c> annotation into a path → metadata map.
    /// Paths follow <see cref="SchemaAnnotationWalker"/> (dotted, <c>[]</c> for array items).
    /// Annotations that are absent, malformed, or explicitly disabled are omitted.
    /// </summary>
    /// <param name="schemaRoot">The root JsonElement of the schema.</param>
    /// <returns>Map of property path to metadata; empty when nothing is annotated.</returns>
    public static IReadOnlyDictionary<string, SensitiveFieldMetadata> Parse(JsonElement schemaRoot)
    {
        var result = new Dictionary<string, SensitiveFieldMetadata>(StringComparer.Ordinal);

        foreach (var node in SchemaAnnotationWalker.Walk(schemaRoot))
        {
            if (!TryReadAnnotation(node.Schema, out var annotation))
                continue;

            var metadata = ReadMetadata(annotation);
            if (metadata.Enabled)
                result[node.Path] = metadata;
        }

        return result;
    }

    /// <summary>
    /// Validates the <c>x-sensitive</c> annotations of a schema for definition-time publishing.
    /// </summary>
    /// <param name="schemaRoot">The root JsonElement of the schema.</param>
    /// <returns>
    /// Every problem found, in document order; empty when the annotations are sound. All are
    /// fatal — see <see cref="SensitiveSchemaProblem"/> for why there is no warning tier.
    /// </returns>
    public static IReadOnlyList<SensitiveSchemaProblem> Validate(JsonElement schemaRoot)
    {
        var problems = new List<SensitiveSchemaProblem>();

        foreach (var node in SchemaAnnotationWalker.Walk(schemaRoot))
        {
            if (!node.Schema.TryGetProperty(SensitiveKey, out var annotation))
                continue;

            if (annotation.ValueKind != JsonValueKind.Object)
            {
                problems.Add(new SensitiveSchemaProblem(
                    node.Path,
                    $"'{SensitiveKey}' must be an object."));
                continue;
            }

            ValidateAnnotation(node, annotation, problems);
        }

        // An annotation the runtime can never see is worse than none: it reads as protection.
        foreach (var location in SchemaAnnotationWalker.FindUnreachable(schemaRoot, SensitiveKey))
        {
            problems.Add(new SensitiveSchemaProblem(
                location,
                $"'{SensitiveKey}' here is never applied: the runtime does not resolve $ref or " +
                "descend into $defs/oneOf/anyOf/allOf/if-then-else/patternProperties/" +
                "additionalProperties. Move the annotation onto a property reachable through " +
                "'properties' or 'items'."));
        }

        return problems;
    }

    private static void ValidateAnnotation(
        SchemaPropertyNode node,
        JsonElement annotation,
        List<SensitiveSchemaProblem> problems)
    {
        var metadata = ReadMetadata(annotation);

        if (!metadata.Enabled)
        {
            // Staging an annotation before turning it on is legal — but asking for protection
            // while disabled is the "I set encryptAtRest and forgot enabled" bug, where the
            // author believes the field is protected and nothing protects it.
            if (metadata.EncryptAtRest || metadata.RedactInLogs)
            {
                problems.Add(new SensitiveSchemaProblem(
                    node.Path,
                    $"'{SensitiveKey}.enabled' is false, so 'encryptAtRest'/'redactInLogs' would be " +
                    "ignored and the field would NOT be protected. Set 'enabled': true, or remove " +
                    "the protection flags."));
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(metadata.Purpose))
        {
            problems.Add(new SensitiveSchemaProblem(
                node.Path,
                $"'{SensitiveKey}.purpose' is required when 'enabled' is true (e.g. \"PII\", \"Financial\")."));
        }

        if (metadata.MaskingPattern is not null &&
            !SensitiveValueMasker.TryValidatePattern(metadata.MaskingPattern, out var patternError))
        {
            problems.Add(new SensitiveSchemaProblem(
                node.Path,
                $"'{SensitiveKey}.maskingPattern' is invalid: {patternError}"));
        }

        if (metadata.RetentionDays is <= 0)
        {
            problems.Add(new SensitiveSchemaProblem(
                node.Path,
                $"'{SensitiveKey}.retentionDays' must be greater than zero."));
        }

        if (!metadata.EncryptAtRest)
            return;

        // The core conflict. Instance filtering runs as raw SQL over the Data jsonb, so a
        // predicate on an encrypted path would match nothing and report no error at all.
        // Definition time is the only place this is visible to the author.
        var conflicting = new List<string>();
        if (node.Schema.TryGetProperty(FilterOperatorsKey, out var filterOperators) &&
            filterOperators.ValueKind == JsonValueKind.Array &&
            filterOperators.GetArrayLength() > 0)
        {
            conflicting.Add(FilterOperatorsKey);
        }

        if (node.Schema.TryGetProperty(SortableKey, out var sortable) &&
            sortable.ValueKind == JsonValueKind.True)
        {
            conflicting.Add(SortableKey);
        }

        if (conflicting.Count > 0)
        {
            problems.Add(new SensitiveSchemaProblem(
                node.Path,
                $"'{SensitiveKey}.encryptAtRest' cannot be combined with " +
                $"{string.Join(" or ", conflicting)}: an encrypted value is stored as ciphertext, " +
                "so filtering and sorting on this path would silently match nothing. Remove the " +
                "filter/sort metadata, or protect the field with 'redactInLogs' only."));
        }

        var type = node.Schema.TryGetProperty(TypeKey, out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        if (type is not null && !string.Equals(type, "string", StringComparison.Ordinal))
        {
            problems.Add(new SensitiveSchemaProblem(
                node.Path,
                $"'{SensitiveKey}.encryptAtRest' is only supported on 'type: string' fields " +
                $"(this field is '{type}'). Ciphertext is stored as a JSON string, which would " +
                "not satisfy the field's own declared type."));
        }
    }

    private static bool TryReadAnnotation(JsonElement schema, out JsonElement annotation)
    {
        if (schema.TryGetProperty(SensitiveKey, out annotation) && annotation.ValueKind == JsonValueKind.Object)
            return true;

        annotation = default;
        return false;
    }

    private static SensitiveFieldMetadata ReadMetadata(JsonElement annotation) => new()
    {
        Enabled = ReadBoolean(annotation, EnabledKey),
        Purpose = ReadString(annotation, PurposeKey),
        EncryptAtRest = ReadBoolean(annotation, EncryptAtRestKey),
        RedactInLogs = ReadBoolean(annotation, RedactInLogsKey),
        MaskingPattern = ReadString(annotation, MaskingPatternKey),
        RetentionDays = ReadInt32(annotation, RetentionDaysKey)
    };

    private static bool ReadBoolean(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number)
            ? number
            : null;
}
