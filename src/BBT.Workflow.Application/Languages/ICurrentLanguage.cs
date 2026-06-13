namespace BBT.Workflow.Languages;

/// <summary>
/// Provides the caller's current language/culture for the active request, resolved from the
/// <c>Accept-Language</c> header. Reusable, request-scoped abstraction (mirrors <c>ICurrentUser</c>)
/// for localization decisions such as resolving display labels.
/// </summary>
public interface ICurrentLanguage
{
    /// <summary>
    /// The resolved culture, e.g. <c>tr-TR</c> or <c>en-US</c>. Defaults to <c>en-US</c> when none is provided.
    /// </summary>
    string Culture { get; }

    /// <summary>
    /// The neutral language portion of <see cref="Culture"/>, e.g. <c>tr</c> for <c>tr-TR</c>.
    /// </summary>
    string Language { get; }
}
