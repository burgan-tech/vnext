using System;
using System.Linq;
using System.Text.Json;

namespace BBT.Workflow.Benchmarks.Legacy;

/// <summary>
/// VERBATIM copy of the pre-single-pass <c>JsonData.NormalizeJson</c> / <c>NormalizeJsonElement</c>
/// (per-call options allocation, one <c>SerializeToElement</c> per object/array node). Benchmark baseline only.
/// </summary>
public static class LegacyNormalizeJson
{
    public static string Normalize(string json)
    {
        try
        {
            var jsonElement = JsonSerializer.Deserialize<JsonElement>(json);
            var normalizedElement = NormalizeJsonElement(jsonElement);
            var options = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = null,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            return JsonSerializer.Serialize(normalizedElement, options);
        }
        catch
        {
            return json;
        }
    }

    private static JsonElement NormalizeJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var sortedProperties = element.EnumerateObject()
                    .OrderBy(prop => prop.Name, StringComparer.Ordinal)
                    .ToDictionary(prop => prop.Name, prop => NormalizeJsonElement(prop.Value));
                return JsonSerializer.SerializeToElement(sortedProperties);
            case JsonValueKind.Array:
                var normalizedArray = element.EnumerateArray().Select(NormalizeJsonElement).ToArray();
                return JsonSerializer.SerializeToElement(normalizedArray);
            default:
                return element;
        }
    }
}
