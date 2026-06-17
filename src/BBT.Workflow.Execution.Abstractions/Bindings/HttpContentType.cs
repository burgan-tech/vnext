namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Resolves the effective media type for an HTTP/SOAP-style request body and classifies it.
/// Shared between the binding mapper (which shapes the body string) and the task invoker
/// (which applies the content type to the request content) to keep the resolution rule in one place.
/// </summary>
public static class HttpContentType
{
    /// <summary>
    /// The default media type used when neither an explicit content type nor a Content-Type header is supplied.
    /// </summary>
    public const string Default = "application/json";

    /// <summary>
    /// Determines whether the given content type denotes a JSON payload (e.g. "application/json",
    /// "application/problem+json"). A null or empty content type is treated as JSON (the default).
    /// </summary>
    public static bool IsJson(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return true;

        return contentType.Contains("json", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the effective content type using the rule:
    /// explicit content type → "Content-Type" header → <see cref="Default"/>.
    /// </summary>
    /// <param name="explicitContentType">Content type set explicitly on the task, or null.</param>
    /// <param name="headerContentType">Content type found in the request headers, or null.</param>
    public static string Resolve(string? explicitContentType, string? headerContentType)
    {
        if (!string.IsNullOrWhiteSpace(explicitContentType))
            return explicitContentType;

        if (!string.IsNullOrWhiteSpace(headerContentType))
            return headerContentType;

        return Default;
    }
}
