using System.Text.Json;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Helper for converting task headers between <see cref="JsonElement"/> and <see cref="Dictionary{TKey,TValue}"/>.
/// Used by tasks that support Headers to implement AddHeader and RemoveHeader.
/// </summary>
internal static class TaskHeadersHelper
{
    /// <summary>
    /// Converts task headers from <see cref="JsonElement"/> to a mutable dictionary.
    /// Returns an empty dictionary when headers is null, not a JSON object, or when parsing fails.
    /// </summary>
    /// <param name="headers">The headers as JsonElement, or null.</param>
    /// <returns>A mutable dictionary; empty if headers is null or invalid.</returns>
    public static Dictionary<string, string?> ToMutableDictionary(JsonElement? headers)
    {
        if (!headers.HasValue)
            return new Dictionary<string, string?>();

        var element = headers.Value;
        try
        {
            if (element.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string?>();

            var dictionary = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                dictionary[property.Name] = GetStringValue(property.Value);
            }

            return dictionary;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>();
        }
    }

    /// <summary>
    /// Converts a dictionary of headers to <see cref="JsonElement"/> for storage on the task.
    /// Returns null when the dictionary is null or empty.
    /// </summary>
    /// <param name="dict">The headers dictionary.</param>
    /// <returns>JsonElement for the headers, or null if dict is null or empty.</returns>
    public static JsonElement? FromDictionary(Dictionary<string, string?>? dict)
    {
        if (dict == null || dict.Count == 0)
            return null;

        return JsonSerializer.SerializeToElement(dict);
    }

    private static string? GetStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            _ => element.GetRawText()
        };
    }
}
