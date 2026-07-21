using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Orchestration.Controllers;

/// <summary>
/// Shared response-shaping logic for the function and instance output mappers. Copies
/// author-supplied response headers onto the outgoing response while excluding hop-by-hop /
/// server-owned headers, and resolves the response Content-Type.
/// </summary>
/// <remarks>
/// <c>content-type</c> is intentionally not written as a raw header — it is applied through
/// <see cref="ObjectResult.ContentTypes"/> so the exact media-type string is preserved (no
/// implicit <c>charset</c> suffix). When the author does not supply one, it defaults to
/// <c>application/json</c>. Functions act as a BFF layer, so authors may set a custom
/// Content-Type for integration scenarios.
/// </remarks>
internal static class ResponseOutputWriter
{
    /// <summary>Default response media type when the author does not set <c>content-type</c>.</summary>
    private const string DefaultContentType = "application/json";

    private const string ContentTypeHeader = "content-type";

    private static readonly HashSet<string> RestrictedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "content-length",
        ContentTypeHeader,      // applied via ObjectResult.ContentTypes, never as a raw header
        "date",                 // server writes its own
        "host",                 // upstream internal hostname must not leak
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "server",               // orchestrator sets its own Server header
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade"
    };

    /// <summary>
    /// Copies author-supplied headers onto the response, skipping restricted headers.
    /// </summary>
    public static void ApplyHeaders(IReadOnlyDictionary<string, string>? headers, HttpContext httpContext)
    {
        if (headers is null || headers.Count == 0)
            return;

        foreach (var (key, value) in headers)
        {
            if (RestrictedResponseHeaders.Contains(key))
                continue;

            httpContext.Response.Headers[key] = value;
        }
    }

    /// <summary>
    /// Resolves the response Content-Type from author-supplied headers (case-insensitive),
    /// falling back to <c>application/json</c> when absent or empty.
    /// </summary>
    public static string ResolveContentType(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                if (string.Equals(key, ContentTypeHeader, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return DefaultContentType;
    }
}
