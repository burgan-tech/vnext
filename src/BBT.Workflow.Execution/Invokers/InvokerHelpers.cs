using System.Net.Http.Headers;
using System.Text.Json;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Shared helper methods for task invokers.
/// </summary>
internal static class InvokerHelpers
{
    /// <summary>
    /// Attempts to parse JSON content. Returns the original content if parsing fails.
    /// </summary>
    /// <param name="content">The content to parse.</param>
    /// <returns>Parsed JSON object or the original content if parsing fails.</returns>
    public static object? TryParseJson(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        try
        {
            return JsonSerializer.Deserialize<object>(content);
        }
        catch (JsonException)
        {
            // Treat parse errors as "not JSON" and return the original content
            return content;
        }
    }

    /// <summary>
    /// Merges response headers and content headers into a single dictionary.
    /// Uses case-insensitive key comparison and concatenates duplicate header values.
    /// </summary>
    /// <param name="responseHeaders">HTTP response headers.</param>
    /// <param name="contentHeaders">HTTP content headers.</param>
    /// <returns>Merged dictionary of headers.</returns>
    public static Dictionary<string, string> MergeHeaders(
        HttpResponseHeaders responseHeaders,
        HttpContentHeaders contentHeaders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in responseHeaders.Concat(contentHeaders))
        {
            var value = string.Join(", ", header.Value);
            if (result.TryGetValue(header.Key, out var existing))
            {
                result[header.Key] = $"{existing}, {value}";
            }
            else
            {
                result[header.Key] = value;
            }
        }

        return result;
    }
}

