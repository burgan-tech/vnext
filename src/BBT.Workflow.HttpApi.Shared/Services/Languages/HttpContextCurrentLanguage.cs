using BBT.Workflow.Domain.Shared;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Languages;

/// <summary>
/// HTTP context-based <see cref="ICurrentLanguage"/> that resolves the culture from the
/// request's <c>Accept-Language</c> header via <see cref="LanguageResolver"/> and caches it in
/// <see cref="HttpContext.Items"/> for the lifetime of the request.
/// </summary>
/// <param name="httpContextAccessor">Accessor for the current HTTP context.</param>
internal sealed class HttpContextCurrentLanguage(IHttpContextAccessor httpContextAccessor) : ICurrentLanguage
{
    internal const string CultureItemKey = "CurrentLanguageCulture";

    /// <inheritdoc />
    public string Culture => ResolveCulture();

    /// <inheritdoc />
    public string Language => Culture.Split('-', 2)[0];

    private string ResolveCulture()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
            return LanguageResolver.DefaultCulture;

        if (httpContext.Items.TryGetValue(CultureItemKey, out var cached) && cached is string cachedCulture)
            return cachedCulture;

        var acceptLanguage = httpContext.Request.Headers.TryGetValue(HeadersConstants.AcceptLanguage, out var header)
            ? header.ToString()
            : null;

        var culture = LanguageResolver.ResolveCulture(acceptLanguage);
        httpContext.Items[CultureItemKey] = culture;
        return culture;
    }
}
