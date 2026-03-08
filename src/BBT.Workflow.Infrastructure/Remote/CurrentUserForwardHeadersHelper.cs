using BBT.Workflow.CurrentUser;

namespace BBT.Workflow.Remote;

/// <summary>
/// Merges current user forward headers and optional input headers into outbound HTTP requests for remote/subflow calls.
/// Callers obtain forward headers via ICurrentUser.ToForwardHeaders() and pass them here with optional input.Headers.
/// </summary>
public static class CurrentUserForwardHeadersHelper
{
    /// <summary>
    /// Merges forward headers with input headers. Input headers take precedence (override) for the same key.
    /// </summary>
    public static void MergeIntoRequest(HttpRequestMessage request, Dictionary<string, string?> forwardHeaders, IReadOnlyDictionary<string, string?>? inputHeaders, Func<string, bool>? isRestrictedHeader = null)
    {
        isRestrictedHeader ??= _ => false;
        foreach (var kv in forwardHeaders)
        {
            if (string.IsNullOrEmpty(kv.Value) || isRestrictedHeader(kv.Key))
                continue;
            request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        if (inputHeaders != null)
        {
            foreach (var kv in inputHeaders)
            {
                if (isRestrictedHeader(kv.Key))
                    continue;
                request.Headers.Remove(kv.Key);
                if (!string.IsNullOrEmpty(kv.Value))
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
    }
}
