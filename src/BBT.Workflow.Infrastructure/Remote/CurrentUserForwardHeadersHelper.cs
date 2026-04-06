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
    /// </summary>
    public static void MergeIntoRequest(HttpRequestMessage request, Dictionary<string, string?> forwardHeaders, IReadOnlyDictionary<string, string?>? inputHeaders, Func<string, bool>? isRestrictedHeader = null)
    {
        isRestrictedHeader ??= _ => false;
        foreach (var kv in forwardHeaders)
        {
            if (string.IsNullOrEmpty(kv.Value) || isRestrictedHeader(kv.Key) || ContentHeaders.Contains(kv.Key))
                continue;
            request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        if (inputHeaders != null)
        {
            foreach (var kv in inputHeaders)
            {
                if (isRestrictedHeader(kv.Key) || ContentHeaders.Contains(kv.Key))
                    continue;
                request.Headers.Remove(kv.Key);
                if (!string.IsNullOrEmpty(kv.Value))
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
    }
}