using System.Text.RegularExpressions;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Evaluates whether a given HTTP status code matches a pattern entry.
/// Supports exact codes ("403", "404") and alias wildcard patterns ("4xx", "40x", "5xx", "50x").
/// Used by <see cref="AcceptedStatusCodesExtensions"/> to determine if a task response should
/// be treated as successful regardless of its HTTP error status.
/// </summary>
/// <remarks>
/// Supported pattern formats (case-insensitive):
/// <list type="table">
///   <item><term>4xx</term><description>Matches 400–499 (hundreds wildcard)</description></item>
///   <item><term>5xx</term><description>Matches 500–599 (hundreds wildcard)</description></item>
///   <item><term>40x</term><description>Matches 400–409 (tens wildcard)</description></item>
///   <item><term>50x</term><description>Matches 500–509 (tens wildcard)</description></item>
///   <item><term>403</term><description>Exact match</description></item>
/// </list>
/// Invalid or unrecognized patterns are silently skipped (never match).
/// </remarks>
public static partial class StatusCodePattern
{
    /// <summary>
    /// Matches a hundreds-level wildcard pattern, e.g. "4xx", "5xx".
    /// Capture group 1: the hundreds digit.
    /// </summary>
    [GeneratedRegex(@"^([1-9])xx$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex HundredsWildcardPattern();

    /// <summary>
    /// Matches a tens-level wildcard pattern, e.g. "40x", "50x".
    /// Capture group 1: the two-digit prefix (hundreds + tens).
    /// </summary>
    [GeneratedRegex(@"^([1-9][0-9])x$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex TensWildcardPattern();

    /// <summary>
    /// Matches an exact three-digit HTTP status code, e.g. "403", "200".
    /// Capture group 1: the full numeric string.
    /// </summary>
    [GeneratedRegex(@"^([1-9][0-9]{2})$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex ExactCodePattern();

    /// <summary>
    /// Returns <c>true</c> if <paramref name="statusCode"/> is matched by <paramref name="pattern"/>.
    /// Invalid or unrecognized patterns return <c>false</c> without throwing.
    /// </summary>
    /// <param name="pattern">
    /// A pattern string such as "403", "4xx", "40x". Comparison is case-insensitive.
    /// Unrecognized patterns are silently ignored.
    /// </param>
    /// <param name="statusCode">The HTTP status code to test. Returns <c>false</c> when <c>null</c>.</param>
    public static bool Matches(string pattern, int? statusCode)
    {
        if (statusCode == null || string.IsNullOrWhiteSpace(pattern))
            return false;

        var code = statusCode.Value;
        var p = pattern.Trim();

        try
        {
            // "4xx", "5xx" — hundreds-level wildcard
            var hundredsMatch = HundredsWildcardPattern().Match(p);
            if (hundredsMatch.Success && int.TryParse(hundredsMatch.Groups[1].Value, out var hundreds))
                return code >= hundreds * 100 && code <= hundreds * 100 + 99;

            // "40x", "50x" — tens-level wildcard
            var tensMatch = TensWildcardPattern().Match(p);
            if (tensMatch.Success && int.TryParse(tensMatch.Groups[1].Value, out var prefix))
                return code >= prefix * 10 && code <= prefix * 10 + 9;

            // "403", "200" — exact three-digit code
            var exactMatch = ExactCodePattern().Match(p);
            if (exactMatch.Success && int.TryParse(exactMatch.Groups[1].Value, out var exact))
                return code == exact;
        }
        catch (RegexMatchTimeoutException)
        {
            // Pattern evaluation timed out (e.g. pathological input) — treat as no match
            return false;
        }

        // Unrecognized pattern format — silently ignore
        return false;
    }
}

/// <summary>
/// Extension methods for evaluating <c>AcceptedStatusCodes</c> lists on workflow tasks.
/// </summary>
public static class AcceptedStatusCodesExtensions
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="statusCode"/> is matched by at least one entry
    /// in <paramref name="acceptedCodes"/>. Supports exact codes and alias patterns ("4xx", "40x", etc.).
    /// Invalid or unrecognized patterns are silently skipped.
    /// Returns <c>false</c> when the list is <c>null</c>, empty, or <paramref name="statusCode"/> is <c>null</c>.
    /// </summary>
    public static bool IsAcceptedStatusCode(this IReadOnlyList<string>? acceptedCodes, int? statusCode)
    {
        if (acceptedCodes == null || acceptedCodes.Count == 0 || statusCode == null)
            return false;

        foreach (var pattern in acceptedCodes)
        {
            if (StatusCodePattern.Matches(pattern, statusCode))
                return true;
        }

        return false;
    }
}
