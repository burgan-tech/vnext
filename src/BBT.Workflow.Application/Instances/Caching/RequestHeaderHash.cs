using System.Security.Cryptography;
using System.Text;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Hashes the request headers a cached response could depend on beyond <see cref="CallerScopeHash"/>
/// (role, identity, culture): a dynamic role grant or a rule-based schema selection may read any header
/// name the workflow author picked. Shared by every cache that folds headers into its key.
/// </summary>
internal static class RequestHeaderHash
{
    /// <summary>
    /// Headers excluded because they vary per request while no authorization or schema decision
    /// can depend on them. Everything else is folded in, deliberately erring towards a lower hit
    /// rate rather than towards serving one caller scope's response to another. Widening this list
    /// is a security decision, not a performance one.
    /// </summary>
    private static readonly HashSet<string> VolatileHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "traceparent", "tracestate", "baggage",
        "x-request-id", "x-correlation-id", "request-id",
        "date", "user-agent", "content-length", "accept-encoding", "connection"
    };

    /// <summary>Returns <c>"0"</c> when no non-volatile header is present, else a 16-hex-char digest.</summary>
    internal static string Compute(IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is null || headers.Count == 0)
            return "0";

        var builder = new StringBuilder();
        foreach (var header in headers
                     .Where(h => !VolatileHeaders.Contains(h.Key))
                     .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(header.Key.ToLowerInvariant()).Append('=').Append(header.Value).Append('|');
        }

        if (builder.Length == 0)
            return "0";

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }
}
