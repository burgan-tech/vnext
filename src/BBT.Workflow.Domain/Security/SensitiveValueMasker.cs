using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace BBT.Workflow.Security;

/// <summary>
/// Applies a <c>x-sensitive.maskingPattern</c> to a value, producing the partially-revealing
/// placeholder that goes into logs and diagnostic messages in place of the real value.
/// <para>
/// Pure and total: it never throws and never returns the input unchanged. Anything it cannot
/// interpret degrades to <see cref="Redacted"/>, because a masker that fails open leaks exactly
/// the value it exists to hide. Pattern validity is therefore a definition-time concern
/// (<see cref="TryValidatePattern"/>), not a runtime one.
/// </para>
/// </summary>
public static partial class SensitiveValueMasker
{
    /// <summary>The placeholder used when there is no pattern, or the pattern cannot be applied.</summary>
    public const string Redacted = "***";

    /// <summary>
    /// Token vocabulary: <c>{first}</c>/<c>{last}</c> reveal one character, <c>{firstN}</c>/
    /// <c>{lastN}</c> reveal N (1–99, e.g. <c>{last4}</c>). Everything else in the pattern is
    /// literal text.
    /// </summary>
    [GeneratedRegex(@"\{(?<token>[A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^(?<edge>first|last)(?<count>[1-9][0-9]?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenGrammar();

    /// <summary>
    /// Masks <paramref name="value"/> using <paramref name="pattern"/>.
    /// </summary>
    /// <param name="value">The sensitive value. Null or whitespace yields <see cref="Redacted"/>.</param>
    /// <param name="pattern">
    /// The masking pattern. Null, empty, or invalid yields <see cref="Redacted"/>.
    /// </param>
    /// <returns>The masked representation; never the raw value, never null.</returns>
    public static string Mask(string? value, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrEmpty(pattern))
            return Redacted;

        if (!TryValidatePattern(pattern, out _))
            return Redacted;

        var result = new StringBuilder(pattern.Length + value.Length);
        var cursor = 0;

        foreach (var match in TokenPattern().EnumerateMatches(pattern))
        {
            result.Append(pattern, cursor, match.Index - cursor);

            var token = pattern.AsSpan(match.Index + 1, match.Length - 2);
            result.Append(Reveal(value, token));

            cursor = match.Index + match.Length;
        }

        result.Append(pattern, cursor, pattern.Length - cursor);

        // A pattern made only of tokens against a short value can collapse to nothing at all;
        // that is indistinguishable from "no value", so fall back rather than emit an empty mask.
        return result.Length == 0 ? Redacted : result.ToString();
    }

    /// <summary>
    /// Checks that every <c>{...}</c> token in the pattern is one this masker understands.
    /// Called at definition time so an author learns about a typo before it silently degrades a
    /// production mask to <see cref="Redacted"/>.
    /// </summary>
    /// <param name="pattern">The pattern to check.</param>
    /// <param name="error">Author-facing explanation when the pattern is rejected.</param>
    /// <returns><c>true</c> when the pattern is usable.</returns>
    public static bool TryValidatePattern(string? pattern, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "the pattern is empty.";
            return false;
        }

        var tokenCount = 0;

        foreach (var match in TokenPattern().EnumerateMatches(pattern))
        {
            tokenCount++;
            var token = pattern.AsSpan(match.Index + 1, match.Length - 2);

            if (!TokenGrammar().IsMatch(token))
            {
                error = $"'{{{token}}}' is not a known token. Use {{first}}, {{last}}, " +
                        "{firstN} or {lastN} (N between 1 and 99), for example {last4}.";
                return false;
            }
        }

        // No tokens at all is a constant string, which reveals nothing and is therefore fine —
        // but a lone '{' is far more likely to be a mistyped token than intended literal text.
        if (tokenCount == 0 && pattern.Contains('{', StringComparison.Ordinal))
        {
            error = "the pattern contains '{' but no valid token. Use {first}, {last}, " +
                    "{firstN} or {lastN}, for example {last4}.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Resolves one validated token against the value, revealing at most the whole value.
    /// </summary>
    private static ReadOnlySpan<char> Reveal(string value, ReadOnlySpan<char> token)
    {
        var fromStart = token.StartsWith("first", StringComparison.Ordinal);
        var digits = token[(fromStart ? "first".Length : "last".Length)..];

        var count = digits.IsEmpty ? 1 : int.Parse(digits, provider: null);
        count = Math.Min(count, value.Length);

        return fromStart ? value.AsSpan(0, count) : value.AsSpan(value.Length - count);
    }
}
