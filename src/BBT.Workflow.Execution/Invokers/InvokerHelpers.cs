using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Shared helper methods for task invokers.
/// </summary>
internal static class InvokerHelpers
{
    /// <summary>
    /// JSON options for TriggerTask response parsing: MaxDepth 256 and IgnoreCycles to handle large/deep payloads safely.
    /// </summary>
    private static readonly JsonSerializerOptions TriggerTaskJsonOptions = new()
    {
        MaxDepth = 256,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    /// <summary>
    /// Header names owned by the tracing/correlation infrastructure. Task binding definitions
    /// must never overwrite these on outbound calls: a stale traceparent copied into a binding
    /// would detach the downstream service from the live trace, and a forged x-request-id would
    /// break log correlation. The live values are injected by HttpClient's DiagnosticsHandler
    /// (traceparent/tracestate) and by the invoker itself.
    /// </summary>
    private static readonly string[] ReservedTraceHeaders = ["traceparent", "tracestate", "baggage", "x-request-id"];

    /// <summary>
    /// Returns true when the header name is reserved for trace/correlation propagation and must
    /// not be copied from a task binding's header definition onto an outbound request.
    /// </summary>
    public static bool IsReservedTraceHeader(string headerName) =>
        ReservedTraceHeaders.Contains(headerName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to parse JSON content. Returns the original content if parsing fails.
    /// Used for TriggerTask (e.g. GetInstances / GetInstanceData) response body parsing.
    /// </summary>
    /// <param name="content">The content to parse.</param>
    /// <returns>Parsed JSON object or the original content if parsing fails.</returns>
    public static object? TryParseJson(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        try
        {
            return JsonSerializer.Deserialize<object>(content, TriggerTaskJsonOptions);
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

