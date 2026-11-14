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
    /// Returns true when the header name is reserved for trace/correlation propagation and must
    /// not be copied from a task binding's header definition onto an outbound request. Delegates
    /// to <see cref="HttpTaskInvocation.IsReservedTraceHeader"/> — the single implementation
    /// shared with the Orchestrator's in-process HTTP task — so the reserved set cannot drift
    /// between hosts.
    /// </summary>
    public static bool IsReservedTraceHeader(string headerName) =>
        HttpTaskInvocation.IsReservedTraceHeader(headerName);

    /// <summary>
    /// Stamps the trusted workflow correlation headers onto an outbound HTTP request from the
    /// ambient Activity baggage. Delegates to
    /// <see cref="HttpTaskInvocation.ApplyTrustedCorrelationHeaders"/> — the single
    /// implementation shared with the Orchestrator's in-process HTTP task — see it for the
    /// precedence rules (workflow context authoritative, sub/act_sub fill-if-absent).
    /// <paramref name="fillIdentity"/> <c>false</c> skips the sub/act_sub fill: the instance-call invokers
    /// (DirectTrigger, GetInstance, GetInstances, GetInstanceData) pass it because the orchestrator already
    /// resolved their credential (<see cref="HttpTaskInvocation.ResolveInstanceCallCredential"/>) and this
    /// host must not add identity from the trace. The default <c>true</c> is the HTTP / SOAP / Dapr behaviour.
    /// </summary>
    public static void ApplyTrustedCorrelationHeaders(HttpRequestMessage request, bool fillIdentity = true) =>
        HttpTaskInvocation.ApplyTrustedCorrelationHeaders(request, fillIdentity: fillIdentity);

    /// <summary>
    /// Fills <c>X-Request-Id</c> from the ambient request id when the binding left it absent or
    /// empty — the mapping's value wins. Delegates to <see cref="HttpTaskInvocation.ApplyRequestId"/>.
    /// For invokers that apply only this rule; <see cref="ApplyTrustedCorrelationHeaders"/> already
    /// includes it.
    /// </summary>
    public static void ApplyRequestId(HttpRequestMessage request) =>
        HttpTaskInvocation.ApplyRequestId(request);

    /// <summary>
    /// Copies a task binding's header definition (JSON object) onto an outbound request, skipping the reserved
    /// trace/correlation headers (<see cref="IsReservedTraceHeader"/>). Call
    /// <see cref="ApplyTrustedCorrelationHeaders"/> afterwards (with <c>fillIdentity: false</c> for an instance call,
    /// whose credential the orchestrator already resolved; otherwise sub / act_sub are filled only where the binding did
    /// not set them).
    /// </summary>
    public static void AddBindingHeaders(HttpRequestMessage request, string? headersJson)
    {
        if (string.IsNullOrEmpty(headersJson))
            return;

        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        if (headers == null)
            return;

        foreach (var header in headers.Where(h => h.Value != null && !IsReservedTraceHeader(h.Key)))
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    /// <summary>
    /// Removes the credential headers (sub, act_sub, position, client_id, role) whose value is blank from an outbound
    /// request. Delegates to <see cref="HttpTaskInvocation.DropBlankCredentialHeaders"/>. A blank value the binding set is
    /// a marker for "make this call without this credential"; call this LAST, after every header has been added, so the
    /// request never leaves with a blank credential.
    /// </summary>
    public static void DropBlankCredentialHeaders(HttpRequestMessage request) =>
        HttpTaskInvocation.DropBlankCredentialHeaders(request);

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

