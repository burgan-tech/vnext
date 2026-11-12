using System.Text.Json;
using BBT.Aether.Domain.Values;
using BBT.Workflow.Shared.Merging;

namespace BBT.Workflow;

/// <summary>
/// Json Data
/// </summary>
public class JsonData : ValueObject
{
    private const string EmptyJson = "{}";
    public static readonly JsonData Empty = new("{}");
    private JsonData()
    {
    }

    public JsonData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            json = EmptyJson;

        Json = json;
    }

    public JsonData(JsonElement? json)
    {
        Json = json is null ? EmptyJson : JsonSerializer.Serialize(json, JsonSerializerConstants.JsonOptions);
    }
    
    public JsonData(object? json)
    {
        Json = json is null ? EmptyJson : JsonSerializer.Serialize(json, JsonSerializerConstants.JsonOptions);
    }

    public string Json { get; private set; } = "{}";
    
    private string? _normalizedJson;
    
    /// <summary>
    /// Gets the normalized JSON string for consistent hashing and comparison
    /// </summary>
    public string NormalizedJson
    {
        get
        {
            if (_normalizedJson == null)
            {
                _normalizedJson = NormalizeJson(Json);
            }
            return _normalizedJson;
        }
    }
    
    // Boxed deliberately: JsonElement is a multi-word struct, so a Nullable<JsonElement> field
    // write is NOT atomic — a torn read would be possible when parallel COW branches race the
    // first access on a SHARED JsonData (snapshots share rows since Katman 2). An object-reference
    // publish is atomic; the unbox on read is negligible next to the parse it replaces.
    private object? _jsonElementBoxed;

    /// <summary>
    /// Parsed once per instance (Json is assigned only in constructors). Benign race: concurrent
    /// first accesses may parse twice; both results are equivalent standalone elements and the
    /// last (reference-atomic) publish wins — no torn value is ever observable.
    /// </summary>
    public JsonElement JsonElement =>
        (JsonElement)(_jsonElementBoxed ??=
            JsonSerializer.Deserialize<JsonElement>(Json, JsonSerializerConstants.JsonOptions)!);

    /// <summary>
    /// Creates a JsonData from an already-materialized element: the raw JSON text is captured for
    /// storage, and the element (cloned, so it is detached from any caller-owned document) is seeded
    /// into the lazy cache — the first <see cref="JsonElement"/> read does not re-parse the payload.
    /// Use on hot paths that hold an element and whose consumers read it back
    /// (e.g. the task-output delta feeding the instance-data append).
    /// </summary>
    public static JsonData FromElement(JsonElement element)
    {
        // The public element factory must detach from a caller-owned JsonDocument, but it does not
        // need to run the JsonElement through JsonSerializer again. Clone once for lifetime safety
        // and take the already-serialized text directly from that clone.
        var owned = element.Clone();
        var data = new JsonData(owned.GetRawText());
        data._jsonElementBoxed = owned;
        return data;
    }

    /// <summary>
    /// Serializes an object into the two representations required by JsonData in one materialization
    /// operation: an owned JsonElement for downstream merge/validation and its raw JSON text for
    /// persistence. Unlike <see cref="FromElement"/>, ownership is established inside this method,
    /// so no defensive JsonElement clone is required.
    /// </summary>
    public static JsonData FromMaterializedObject(
        object value,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        var element = JsonSerializer.SerializeToElement(
            value,
            options ?? JsonSerializerConstants.JsonOptions);
        var data = new JsonData(element.GetRawText());
        data._jsonElementBoxed = element;
        return data;
    }

    /// <summary>
    /// Canonicalizer çıktısı için: json ZATEN kanonik/normalize — NormalizedJson yeniden hesaplanmaz.
    /// </summary>
    internal static JsonData FromNormalized(string normalizedJson)
    {
        var data = new JsonData(normalizedJson);
        data._normalizedJson = normalizedJson;
        return data;
    }

    public JsonData Merge(JsonData newData)
    {
        // Use the unified merge strategy for JsonElement objects
        var mergedElement = ObjectMerger.MergeValues(JsonElement, newData.JsonElement);
        
        // Convert back to JsonData
        if (mergedElement is JsonElement jsonElement)
        {
            return new JsonData(jsonElement);
        }
        
        // Fallback: if merge result is not JsonElement, serialize it
        var serializedResult = JsonSerializer.Serialize(mergedElement, JsonSerializerConstants.JsonOptions);
        return new JsonData(serializedResult);
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Json;
    }

    /// <summary>
    /// Writer for <see cref="NormalizeJson"/>: compact, relaxed escaping — the options the historical
    /// implementation serialized its final output with.
    /// </summary>
    private static readonly JsonWriterOptions NormalizeWriterOptions = new()
    {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Normalizes JSON string to ensure consistent hashing regardless of formatting.
    /// <para>
    /// The output feeds the PERSISTED <c>InstanceData.DataHash</c> (and the field-encryption keyed hash),
    /// so it is byte-identical to the historical multi-pass implementation (parse → per-object
    /// <c>OrderBy(Ordinal).ToDictionary</c> + <c>SerializeToElement</c> → final serialize), pinned by
    /// <c>JsonDataNormalizeJsonParityTests</c> against a verbatim copy of it:
    /// <list type="bullet">
    /// <item>parsed with default document options (max depth 64, no comments, no trailing commas);</item>
    /// <item>object members ordered by <see cref="StringComparer.Ordinal"/>, arrays positional;</item>
    /// <item>leaf values written through <see cref="JsonElement.WriteTo"/> — number tokens keep their raw
    /// text, strings and member names are re-escaped with the relaxed encoder;</item>
    /// <item>a duplicate member name ANYWHERE (the old <c>ToDictionary</c> threw) or any parse/write failure
    /// returns the input unchanged.</item>
    /// </list>
    /// One parse and one <see cref="Utf8JsonWriter"/> pass, instead of re-serializing every subtree once
    /// per enclosing object (O(size × depth)).
    /// </para>
    /// </summary>
    /// <param name="json">The JSON string to normalize</param>
    /// <returns>Normalized JSON string</returns>
    private static string NormalizeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var buffer = new BBT.Workflow.Canonicalization.PooledByteBufferWriter(json.Length + 16);
            using (var writer = new Utf8JsonWriter(buffer, NormalizeWriterOptions))
            {
                if (!TryWriteNormalized(writer, document.RootElement))
                    return json; // duplicate member name: historical ToDictionary throw ⇒ input unchanged
            }
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch
        {
            // If JSON parsing (or writing) fails, return original string
            return json;
        }
    }

    /// <summary>
    /// Writes <paramref name="element"/> with every object's members sorted ordinally. Returns false on the
    /// first duplicate member name (the caller then returns the original input).
    /// </summary>
    private static bool TryWriteNormalized(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var count = element.GetPropertyCount();
                var properties = System.Buffers.ArrayPool<JsonProperty>.Shared.Rent(Math.Max(count, 1));
                var names = System.Buffers.ArrayPool<string>.Shared.Rent(Math.Max(count, 1));
                try
                {
                    var n = 0;
                    foreach (var property in element.EnumerateObject())
                    {
                        properties[n] = property;
                        names[n] = property.Name;
                        n++;
                    }

                    Array.Sort(names, properties, 0, n, StringComparer.Ordinal);

                    writer.WriteStartObject();
                    for (var i = 0; i < n; i++)
                    {
                        if (i > 0 && string.Equals(names[i - 1], names[i], StringComparison.Ordinal))
                            return false;
                        writer.WritePropertyName(names[i]);
                        if (!TryWriteNormalized(writer, properties[i].Value))
                            return false;
                    }
                    writer.WriteEndObject();
                    return true;
                }
                finally
                {
                    System.Buffers.ArrayPool<JsonProperty>.Shared.Return(properties, clearArray: true);
                    System.Buffers.ArrayPool<string>.Shared.Return(names, clearArray: true);
                }
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    if (!TryWriteNormalized(writer, item))
                        return false;
                }
                writer.WriteEndArray();
                return true;
            default:
                element.WriteTo(writer);
                return true;
        }
    }

    public static JsonData CreateFrom(string json)
    {
        return new JsonData(json);
    }
}
