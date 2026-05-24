using System.Globalization;
using System.Text.RegularExpressions;

namespace BBT.Workflow.Execution;

/// <summary>
/// Matches HTTP status codes against accepted status code patterns.
/// </summary>
public static class AcceptedStatusCodeMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static bool IsAccepted(int? statusCode, IReadOnlyList<string>? acceptedCodes)
    {
        if (statusCode is null || acceptedCodes is null || acceptedCodes.Count == 0)
            return false;

        var statusCodeText = statusCode.Value.ToString(CultureInfo.InvariantCulture);

        foreach (var acceptedCode in acceptedCodes)
        {
            if (string.IsNullOrWhiteSpace(acceptedCode))
                continue;

            var pattern = ToRegexPattern(acceptedCode.Trim());
            if (Regex.IsMatch(statusCodeText, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout))
                return true;
        }

        return false;
    }

    private static string ToRegexPattern(string acceptedCode)
    {
        var wildcardPattern = Regex.Replace(
            Regex.Escape(acceptedCode),
            "x",
            "\\d",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);

        return $"^{wildcardPattern}$";
    }
}
