using System.Globalization;
using System.Text.Json;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Parses filter values for dgt/dlt (and DateTime instance columns) into UTC <see cref="DateTime"/>
/// suitable for <c>NpgsqlDbType.TimestampTz</c> / PostgreSQL <c>timestamptz</c>.
/// </summary>
public static class FilterTimestampTzValueParser
{
    /// <summary>Values above this (absolute) are treated as Unix milliseconds; otherwise seconds.</summary>
    public const long UnixMillisecondsThreshold = 1_000_000_000_000L;

    /// <summary>
    /// Parses a filter value to UTC. Throws <see cref="ArgumentException"/> if parsing fails.
    /// </summary>
    public static DateTime ParseForTimestampTz(object? value)
    {
        if (TryParseForTimestampTz(value, out var utc))
            return utc;

        throw new ArgumentException(
            $"Value '{FormatForMessage(value)}' is not a valid date/time for dgt/dlt " +
            "(ISO-8601 with offset, yyyy-MM-dd as UTC midnight, Unix epoch seconds/milliseconds, or DateTime/DateTimeOffset).");
    }

    /// <summary>
    /// Parses a string filter value to UTC. Throws <see cref="ArgumentException"/> if parsing fails.
    /// </summary>
    public static DateTime ParseForTimestampTz(string? value)
    {
        if (TryParseForTimestampTz(value, out var utc))
            return utc;

        throw new ArgumentException(
            $"Value '{FormatForMessage(value)}' is not a valid date/time for dgt/dlt " +
            "(ISO-8601 with offset, yyyy-MM-dd as UTC midnight, Unix epoch seconds/milliseconds).");
    }

    /// <summary>
    /// Attempts to parse a filter value to UTC <see cref="DateTime"/> (<see cref="DateTimeKind.Utc"/>).
    /// </summary>
    public static bool TryParseForTimestampTz(object? value, out DateTime utc)
    {
        utc = default;
        if (value is null)
            return false;

        switch (value)
        {
            case DateTime dt:
                utc = NormalizeToUtc(dt);
                return true;
            case DateTimeOffset dto:
                utc = dto.UtcDateTime;
                return true;
            case DateOnly d:
                utc = DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
                return true;
            case string s:
                return TryParseString(s, out utc);
            case int i:
                return TryParseUnixIntegral(i, out utc);
            case long l:
                return TryParseUnixIntegral(l, out utc);
            case short sh:
                return TryParseUnixIntegral(sh, out utc);
            case byte b:
                return TryParseUnixIntegral(b, out utc);
            case uint ui:
                return TryParseUnixIntegral(ui, out utc);
            case ulong ul when ul <= long.MaxValue:
                return TryParseUnixIntegral((long)ul, out utc);
            case decimal dec:
                return TryParseUnixFractional((double)dec, out utc);
            case double dbl:
                return TryParseUnixFractional(dbl, out utc);
            case float f:
                return TryParseUnixFractional(f, out utc);
            case JsonElement je:
                return TryParseJsonElement(je, out utc);
        }

        return TryParseString(ConvertToStringFallback(value), out utc);
    }

    /// <summary>
    /// Attempts to parse a non-empty string (e.g. legacy filter segment) to UTC.
    /// </summary>
    public static bool TryParseForTimestampTz(string? value, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return TryParseString(value.Trim(), out utc);
    }

    private static bool TryParseJsonElement(JsonElement je, out DateTime utc)
    {
        utc = default;
        return je.ValueKind switch
        {
            JsonValueKind.String => TryParseString(je.GetString() ?? string.Empty, out utc),
            JsonValueKind.Number when je.TryGetInt64(out var l) => TryParseUnixIntegral(l, out utc),
            JsonValueKind.Number when je.TryGetDouble(out var d) => TryParseUnixFractional(d, out utc),
            _ => false
        };
    }

    private static bool TryParseString(string s, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(s))
            return false;

        // ISO date-only (yyyy-MM-dd) as UTC midnight — before DateTimeOffset.TryParse, which may use local zone.
        if (IsIsoDateOnly(s) &&
            DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            utc = DateTime.SpecifyKind(dateOnly.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            return true;
        }

        if (DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                out var dtoRoundtrip))
        {
            utc = dtoRoundtrip.UtcDateTime;
            return true;
        }

        if (DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var dtoUniversal))
        {
            utc = dtoUniversal.UtcDateTime;
            return true;
        }

        if (DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnlyFallback))
        {
            utc = DateTime.SpecifyKind(dateOnlyFallback.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            return true;
        }

        if (IsAllDigits(s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixDigits))
            return TryParseUnixIntegral(unixDigits, out utc);

        if (DateTime.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var dt))
        {
            utc = NormalizeToUtc(dt);
            return true;
        }

        return false;
    }

    /// <summary>Strict yyyy-MM-dd (length 10, separators at 4 and 7).</summary>
    private static bool IsIsoDateOnly(string s)
    {
        return s.Length == 10
               && s[4] == '-'
               && s[7] == '-'
               && char.IsAsciiDigit(s[0])
               && char.IsAsciiDigit(s[1])
               && char.IsAsciiDigit(s[2])
               && char.IsAsciiDigit(s[3])
               && char.IsAsciiDigit(s[5])
               && char.IsAsciiDigit(s[6])
               && char.IsAsciiDigit(s[8])
               && char.IsAsciiDigit(s[9]);
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0)
            return false;
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsDigit(s[i]))
                return false;
        }

        return true;
    }

    private static bool TryParseUnixIntegral(long unix, out DateTime utc)
    {
        utc = default;
        var abs = unix >= 0 ? unix : -unix;
        try
        {
            if (abs > UnixMillisecondsThreshold)
            {
                utc = DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime;
                return true;
            }

            utc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseUnixFractional(double unixSeconds, out DateTime utc)
    {
        utc = default;
        if (double.IsNaN(unixSeconds) || double.IsInfinity(unixSeconds))
            return false;

        try
        {
            var millis = unixSeconds * 1000.0;
            if (millis is < long.MinValue or > long.MaxValue)
                return false;
            utc = DateTimeOffset.FromUnixTimeMilliseconds((long)millis).UtcDateTime;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static DateTime NormalizeToUtc(DateTime dt)
    {
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };
    }

    private static string ConvertToStringFallback(object value)
    {
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string FormatForMessage(object? value)
    {
        if (value is null)
            return "(null)";
        if (value is string s)
            return s;
        return ConvertToStringFallback(value);
    }
}
