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
    /// Normalizes JSON string to ensure consistent hashing regardless of formatting
    /// </summary>
    /// <param name="json">The JSON string to normalize</param>
    /// <returns>Normalized JSON string</returns>
    private static string NormalizeJson(string json)
    {
        try
        {
            // Parse JSON to remove formatting differences
            var jsonElement = JsonSerializer.Deserialize<JsonElement>(json);
            
            // Normalize the JSON element by sorting properties recursively
            var normalizedElement = NormalizeJsonElement(jsonElement);
            
            // Re-serialize with consistent options for deterministic output
            var options = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = null, // Keep original property names
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            
            return JsonSerializer.Serialize(normalizedElement, options);
        }
        catch
        {
            // If JSON parsing fails, return original string
            return json;
        }
    }

    /// <summary>
    /// Recursively normalizes a JsonElement by sorting object properties
    /// </summary>
    /// <param name="element">The JsonElement to normalize</param>
    /// <returns>Normalized JsonElement</returns>
    private static JsonElement NormalizeJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Sort properties by name for consistent ordering
                var sortedProperties = element.EnumerateObject()
                    .OrderBy(prop => prop.Name, StringComparer.Ordinal)
                    .ToDictionary(
                        prop => prop.Name,
                        prop => NormalizeJsonElement(prop.Value) // Recursive normalization
                    );
                
                return JsonSerializer.SerializeToElement(sortedProperties);
                
            case JsonValueKind.Array:
                // Normalize each array element
                var normalizedArray = element.EnumerateArray()
                    .Select(NormalizeJsonElement)
                    .ToArray();
                
                return JsonSerializer.SerializeToElement(normalizedArray);
                
            default:
                // For primitive values, return as-is
                return element;
        }
    }

    public static JsonData CreateFrom(string json)
    {
        return new JsonData(json);
    }
}
