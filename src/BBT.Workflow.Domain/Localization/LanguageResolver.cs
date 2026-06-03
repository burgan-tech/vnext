using BBT.Workflow.Domain.Shared;

namespace BBT.Workflow;

/// <summary>
/// Reusable resolver for the caller's current language/culture from the <c>Accept-Language</c>
/// HTTP header. Operates on a plain headers dictionary so it works across layers and contexts
/// (HTTP requests, forwarded subflow headers, background jobs). Defaults to <c>en-US</c>.
/// </summary>
public static class LanguageResolver
{
    /// <summary>Default culture returned when no language can be resolved.</summary>
    public const string DefaultCulture = "en-US";

    /// <summary>
    /// Resolves the culture from a headers dictionary by reading <c>Accept-Language</c>.
    /// The dictionary is expected to use a case-insensitive comparer (lowercase-normalized keys),
    /// so both <c>accept-language</c> and <c>Accept-Language</c> resolve.
    /// </summary>
    public static string ResolveCulture(IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is null)
            return DefaultCulture;

        if (!headers.TryGetValue(HeadersConstants.AcceptLanguage, out var acceptLanguage) &&
            !headers.TryGetValue("accept-language", out acceptLanguage))
        {
            return DefaultCulture;
        }

        return ResolveCulture(acceptLanguage);
    }

    /// <summary>
    /// Resolves the culture from a raw <c>Accept-Language</c> header value
    /// (e.g. <c>"tr-TR,tr;q=0.9,en-US;q=0.8"</c> → <c>"tr-TR"</c>). Takes the first language token
    /// and strips any quality weight. Returns <see cref="DefaultCulture"/> when empty.
    /// </summary>
    public static string ResolveCulture(string? acceptLanguageHeaderValue)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguageHeaderValue))
            return DefaultCulture;

        var firstLanguage = acceptLanguageHeaderValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLanguage))
            return DefaultCulture;

        return firstLanguage
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? DefaultCulture;
    }
}
