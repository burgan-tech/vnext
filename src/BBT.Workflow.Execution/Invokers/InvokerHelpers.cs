using System.Diagnostics;
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

    // Kept local so the stateless Execution package does not acquire a Domain dependency.
    // These names are the public HTTP contract defined by TelemetryConstants in vNext Domain.
    private const string WorkflowInstanceHeader = "X-Workflow-Instance-Id";
    private const string CorrelationHeader = "X-Correlation-Id";
    private const string SubHeader = "sub";
    private const string ActSubHeader = "act_sub";
    private const string WorkflowInstanceBaggage = "workflow.instance.id";
    private const string CorrelationBaggage = "correlation.id";
    private const string SubBaggage = "sub";
    private const string ActSubBaggage = "act.sub";

    /// <summary>
    /// Header names owned by the tracing/correlation infrastructure. Task binding definitions
    /// must never overwrite these on outbound calls: a stale traceparent copied into a binding
    /// would detach the downstream service from the live trace, and a forged x-request-id or
    /// workflow-context header would break log correlation or spoof workflow identity. The
    /// live values are injected by HttpClient's DiagnosticsHandler (traceparent/tracestate) and
    /// by <see cref="ApplyTrustedCorrelationHeaders"/>. The identity claims (sub/act_sub) are
    /// deliberately NOT reserved: a developer may set them in the task binding and that value
    /// wins — see <see cref="ApplyTrustedCorrelationHeaders"/>.
    /// </summary>
    private static readonly string[] ReservedTraceHeaders =
    [
        "traceparent", "tracestate", "baggage", "x-request-id",
        WorkflowInstanceHeader, CorrelationHeader
    ];

    /// <summary>
    /// Returns true when the header name is reserved for trace/correlation propagation and must
    /// not be copied from a task binding's header definition onto an outbound request.
    /// </summary>
    public static bool IsReservedTraceHeader(string headerName) =>
        ReservedTraceHeaders.Contains(headerName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stamps the trusted workflow correlation headers onto an outbound HTTP request from the
    /// ambient Activity baggage. Shared by every HTTP-shaped task invoker. Two precedence rules:
    /// <list type="bullet">
    /// <item><b>Workflow context</b> (X-Workflow-Instance-Id, X-Correlation-Id) is authoritative:
    /// mapping-provided values must never spoof the context established by vNext, so any
    /// pre-existing values are removed and the baggage values stamped.</item>
    /// <item><b>Identity claims</b> (sub, act_sub) are fill-if-absent: they are token-derived
    /// claims the platform provides as a DEFAULT — a developer who set them explicitly in the
    /// task binding's input mapping keeps their value; only when the binding did not set them
    /// are they filled from the gateway token (baggage).</item>
    /// </list>
    /// </summary>
    public static void ApplyTrustedCorrelationHeaders(HttpRequestMessage request)
    {
        request.Headers.Remove(WorkflowInstanceHeader);
        request.Headers.Remove(CorrelationHeader);

        var workflowInstance = Activity.Current?.GetBaggageItem(WorkflowInstanceBaggage);
        if (Guid.TryParse(workflowInstance, out var workflowInstanceId)
            && workflowInstanceId != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation(
                WorkflowInstanceHeader,
                workflowInstanceId.ToString("D").ToLowerInvariant());
        }

        var correlation = Activity.Current?.GetBaggageItem(CorrelationBaggage);
        if (Guid.TryParseExact(correlation, "N", out var correlationId)
            && correlationId != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId.ToString("N"));
        }

        if (!request.Headers.NonValidated.Contains(SubHeader))
        {
            var subject = Activity.Current?.GetBaggageItem(SubBaggage);
            if (IsSafeIdentityClaim(subject))
            {
                request.Headers.TryAddWithoutValidation(SubHeader, subject);
            }
        }

        if (!request.Headers.NonValidated.Contains(ActSubHeader))
        {
            var actSub = Activity.Current?.GetBaggageItem(ActSubBaggage);
            if (IsSafeIdentityClaim(actSub))
            {
                request.Headers.TryAddWithoutValidation(ActSubHeader, actSub);
            }
        }
    }

    private static bool IsSafeIdentityClaim(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

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

