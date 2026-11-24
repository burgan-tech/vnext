using System.Text;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Converts between a task's query-string text (<c>a=1&amp;b=x%20y</c>, with or without a leading <c>?</c>) and its
/// ordered name/value pairs, for the <see cref="IHasQuery"/> surface. Names and values are percent-decoded on read and
/// percent-encoded on write; a pair without <c>=</c> reads as a null value and writes back as the bare name.
/// </summary>
public static class TaskQueryString
{
    /// <summary>The pairs of <paramref name="queryString"/>, in order; empty for null or blank text.</summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> Parse(string? queryString)
    {
        if (string.IsNullOrWhiteSpace(queryString))
            return [];

        var pairs = new List<KeyValuePair<string, string?>>();
        foreach (var part in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            pairs.Add(separator < 0
                ? new KeyValuePair<string, string?>(Uri.UnescapeDataString(part), null)
                : new KeyValuePair<string, string?>(
                    Uri.UnescapeDataString(part[..separator]),
                    Uri.UnescapeDataString(part[(separator + 1)..])));
        }

        return pairs;
    }

    /// <summary>The query-string text of <paramref name="pairs"/> (no leading <c>?</c>); null when there are none.</summary>
    public static string? Format(IReadOnlyList<KeyValuePair<string, string?>> pairs)
    {
        if (pairs.Count == 0)
            return null;

        var builder = new StringBuilder();
        foreach (var (name, value) in pairs)
        {
            if (builder.Length > 0)
                builder.Append('&');
            builder.Append(Uri.EscapeDataString(name));
            if (value is not null)
                builder.Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }
}
