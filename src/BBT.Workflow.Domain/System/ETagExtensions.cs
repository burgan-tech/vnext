namespace System;

/// <summary>
/// Extension methods for ETag operations
/// </summary>
public static class ETagExtensions
{
    /// <summary>
    /// Determines if the provided ETag matches the current ETag value.
    /// This is typically used for conditional HTTP requests (If-None-Match header).
    /// ETag values may contain double quotes per RFC 7232, which are stripped before comparison.
    /// </summary>
    /// <param name="currentETag">The current ETag value to compare against</param>
    /// <param name="ifNoneMatch">The ETag value from the If-None-Match header</param>
    /// <returns>True if the ETags match, false otherwise</returns>
    public static bool MatchesIfNoneMatch(this string currentETag, string ifNoneMatch)
    {
        var normalizedCurrent = StripQuotes(currentETag);
        var normalizedIfNoneMatch = StripQuotes(ifNoneMatch);
        return normalizedCurrent.Equals(normalizedIfNoneMatch, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips double quotes from an ETag value.
    /// Per RFC 7232, ETag values are quoted strings (e.g., "abc123").
    /// </summary>
    /// <param name="etag">The ETag value potentially containing quotes</param>
    /// <returns>The ETag value without surrounding quotes</returns>
    public static string StripQuotes(this string? etag)
    {
        if (string.IsNullOrEmpty(etag))
            return string.Empty;

        var value = etag.Trim();
        const string WeakPrefix = "W/";
        if (value.StartsWith(WeakPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(WeakPrefix.Length).TrimStart();
        }
        // Only remove surrounding quotes, not quotes that may appear within the value.
        value = value.Trim('"');
        return value;
    }
} 