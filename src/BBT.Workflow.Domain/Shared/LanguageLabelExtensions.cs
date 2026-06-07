namespace BBT.Workflow;

/// <summary>
/// Localization helpers for <see cref="LanguageLabel"/> collections.
/// </summary>
public static class LanguageLabelExtensions
{
    /// <summary>
    /// Resolves the best-matching label text for the requested culture.
    /// Match order: exact culture (e.g. <c>tr-TR</c>) → neutral language (<c>tr</c>, including
    /// regional variants like <c>tr-*</c>) → English (<c>en-US</c>/<c>en</c>) → first label in the list.
    /// Returns <c>null</c> when the collection is null or empty so callers can fall back further.
    /// </summary>
    public static string? ResolveLabel(this IEnumerable<LanguageLabel>? labels, string? culture)
    {
        if (labels is null)
            return null;

        var list = labels as IReadOnlyList<LanguageLabel> ?? labels.ToList();
        if (list.Count == 0)
            return null;

        var requested = string.IsNullOrWhiteSpace(culture) ? LanguageResolver.DefaultCulture : culture.Trim();
        var neutral = requested.Split('-', 2)[0];

        // 1. Exact culture match (e.g. "tr-TR").
        var exact = list.FirstOrDefault(l => string.Equals(l.Language, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.Label;

        // 2. Neutral language match (e.g. "tr"), then any regional variant of it (e.g. "tr-*").
        var neutralMatch = list.FirstOrDefault(l => string.Equals(l.Language, neutral, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(l => l.Language.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase));
        if (neutralMatch is not null)
            return neutralMatch.Label;

        // 3. English fallback.
        var english = list.FirstOrDefault(l => string.Equals(l.Language, "en-US", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(l => string.Equals(l.Language, "en", StringComparison.OrdinalIgnoreCase));
        if (english is not null)
            return english.Label;

        // 4. First label in declaration order.
        return list[0].Label;
    }
}
