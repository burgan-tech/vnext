namespace BBT.Workflow.Shared;

/// <summary>
/// Removes transport credentials that must never be copied into transition history, outbox
/// events or durable background-job payloads. Identity claim headers and workflow-specific
/// metadata remain available to background execution.
/// </summary>
public static class DurableHeaderFilter
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "x-api-key",
        "api-key",
        "x-auth-token"
    };

    /// <summary>Returns whether a header carries a reusable transport credential.</summary>
    public static bool IsSensitive(string key) => SensitiveHeaders.Contains(key);

    /// <summary>Creates a case-insensitive, credential-free durable header envelope.</summary>
    public static Dictionary<string, string?> ForPersistence(
        IReadOnlyDictionary<string, string?>? headers)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
            return result;

        foreach (var (key, value) in headers)
        {
            if (!IsSensitive(key))
                result[key] = value;
        }

        return result;
    }
}
