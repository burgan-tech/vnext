namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Environment configuration of MCP tasks (section <c>Mcp</c>, issue #1105).
/// </summary>
public sealed class McpOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Mcp";

    /// <summary>
    /// Hosts an MCP task may call; <c>*</c> allows any host (local development only). Empty denies all.
    /// Checked at publish and on every call.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// True when <paramref name="endpoint"/> is an absolute http(s) URL whose host is allowed.
    /// </summary>
    public static bool IsHostAllowed(string? endpoint, IReadOnlyCollection<string> allowedHosts, out string? host)
    {
        host = null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        host = uri.Host;
        foreach (var allowed in allowedHosts)
        {
            if (allowed == "*" || string.Equals(allowed, uri.Host, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(allowed, uri.Authority, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
