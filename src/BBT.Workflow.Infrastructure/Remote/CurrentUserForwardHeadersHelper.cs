using System.Diagnostics;
using System.Globalization;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Remote;

/// <summary>
/// Merges current user forward headers and optional input headers into outbound HTTP requests for remote/subflow calls.
/// Callers obtain forward headers via ICurrentUser.ToForwardHeaders() and pass them here with optional input.Headers.
/// </summary>
public static class CurrentUserForwardHeadersHelper
{
    // Content headers belong to HttpContent.Headers, not HttpRequestMessage.Headers.
    // Attempting Remove/Add on request.Headers for these throws InvalidOperationException.
    private static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Language",
        "Content-Location", "Content-Disposition", "Content-Range", "Content-MD5",
        "Expires", "Last-Modified", "Allow", "Host", "Connection", "Accept", "Accept-Encoding"
    };

    /// <summary>
    /// Merges forward headers with input headers. Input headers take precedence (override) for the same key.
    /// Content headers (e.g. Content-Type) are silently skipped as they cannot be set on HttpRequestMessage.Headers.
    /// W3C trace-context headers (traceparent, tracestate, baggage) are never forwarded from either source,
    /// regardless of <paramref name="isRestrictedHeader"/>: both dictionaries hold captured inbound request
    /// headers (possibly restored from a persisted job payload long after the fact), and because .NET's
    /// DiagnosticsHandler injects traceparent fill-if-absent, a stale copy would win over the live one and
    /// parent the callee to the wrong span. The live Activity must own the trace context on outbound calls.
    /// Also stamps cross-domain correlation headers when absent: X-Root-Instance-Id from the ambient
    /// Activity baggage, and X-Request-Id from <paramref name="correlationId"/> when provided.
    /// </summary>
    public static void MergeIntoRequest(HttpRequestMessage request, Dictionary<string, string?> forwardHeaders, IReadOnlyDictionary<string, string?>? inputHeaders, Func<string, bool>? isRestrictedHeader = null, string? correlationId = null)
    {
        isRestrictedHeader ??= _ => false;
        foreach (var kv in forwardHeaders)
        {
            if (string.IsNullOrEmpty(kv.Value) || !IsAsciiSafe(kv.Value) || isRestrictedHeader(kv.Key) || ContentHeaders.Contains(kv.Key)
                || TelemetryConstants.HeaderNames.IsW3CTraceContextHeader(kv.Key))
                continue;
            request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        if (inputHeaders != null)
        {
            foreach (var kv in inputHeaders)
            {
                if (isRestrictedHeader(kv.Key) || ContentHeaders.Contains(kv.Key)
                    || TelemetryConstants.HeaderNames.IsW3CTraceContextHeader(kv.Key))
                    continue;
                request.Headers.Remove(kv.Key);
                if (!string.IsNullOrEmpty(kv.Value) && IsAsciiSafe(kv.Value))
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        // Descent depth rides a header, not baggage: it is a property of THIS call's position in the
        // read ladder, not of the whole trace. Baggage would leak the caller's depth into every
        // unrelated downstream call made from the same context.
        var descentDepth = SubflowDescentContext.Current;
        if (descentDepth > 0 && !request.Headers.Contains(TelemetryConstants.HeaderNames.SubflowDepth))
            request.Headers.TryAddWithoutValidation(
                TelemetryConstants.HeaderNames.SubflowDepth,
                descentDepth.ToString(CultureInfo.InvariantCulture));

        var rootIdBaggage = Activity.Current?.GetBaggageItem(TelemetryConstants.TagNames.RootInstanceId);
        if (!string.IsNullOrEmpty(rootIdBaggage) && !request.Headers.Contains(TelemetryConstants.HeaderNames.RootInstanceId))
            request.Headers.TryAddWithoutValidation(TelemetryConstants.HeaderNames.RootInstanceId, rootIdBaggage);

        if (!string.IsNullOrEmpty(correlationId) && !request.Headers.Contains(TelemetryConstants.HeaderNames.RequestId))
            request.Headers.TryAddWithoutValidation(TelemetryConstants.HeaderNames.RequestId, correlationId);
    }

    private static bool IsAsciiSafe(string? value)
    {
        if (value is null) return true;
        foreach (var c in value)
            if (c > 127) return false;
        return true;
    }
}