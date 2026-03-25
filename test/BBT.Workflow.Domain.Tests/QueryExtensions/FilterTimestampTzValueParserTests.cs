using System;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

public class FilterTimestampTzValueParserTests
{
    private static readonly DateTime Jan1_2024_Utc =
        DateTime.SpecifyKind(new DateTime(2024, 1, 1, 0, 0, 0), DateTimeKind.Utc);

    [Fact]
    public void ParseForTimestampTz_UnixSeconds_Long_ReturnsUtc()
    {
        var utc = FilterTimestampTzValueParser.ParseForTimestampTz(1704067200L);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1704067200).UtcDateTime, utc);
    }

    [Fact]
    public void ParseForTimestampTz_UnixMilliseconds_Long_ReturnsUtc()
    {
        const long ms = 1_704_067_200_000L;
        var utc = FilterTimestampTzValueParser.ParseForTimestampTz(ms);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, utc);
    }

    [Fact]
    public void ParseForTimestampTz_DateOnlyString_YieldsUtcMidnight()
    {
        var utc = FilterTimestampTzValueParser.ParseForTimestampTz("2024-01-01");
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(Jan1_2024_Utc, utc);
    }

    [Fact]
    public void ParseForTimestampTz_Iso8601WithZ_ReturnsUtc()
    {
        var utc = FilterTimestampTzValueParser.ParseForTimestampTz("2024-01-01T00:00:00Z");
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(Jan1_2024_Utc, utc);
    }

    [Fact]
    public void ParseForTimestampTz_AllDigitsString_UsesUnixSeconds()
    {
        var utc = FilterTimestampTzValueParser.ParseForTimestampTz("1704067200");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1704067200).UtcDateTime, utc);
    }

    [Fact]
    public void ParseForTimestampTz_JsonElement_Number_UsesUnix()
    {
        using var doc = JsonDocument.Parse("1704067200");
        var utc = FilterTimestampTzValueParser.ParseForTimestampTz(doc.RootElement);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1704067200).UtcDateTime, utc);
    }

    [Fact]
    public void ParseForTimestampTz_JsonElement_String_Iso()
    {
        using var doc = JsonDocument.Parse("\"2024-06-15T12:30:00+03:00\"");
        var utc = FilterTimestampTzValueParser.ParseForTimestampTz(doc.RootElement);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 9, 30, 0, TimeSpan.Zero).UtcDateTime, utc);
    }

    [Fact]
    public void TryParseForTimestampTz_Invalid_ReturnsFalse()
    {
        Assert.False(FilterTimestampTzValueParser.TryParseForTimestampTz("not-a-date", out _));
        Assert.False(FilterTimestampTzValueParser.TryParseForTimestampTz(null, out _));
    }

    [Fact]
    public void ParseForTimestampTz_Invalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => FilterTimestampTzValueParser.ParseForTimestampTz("nope"));
    }

    [Fact]
    public void ParseForTimestampTz_DateTimeOffset_PreservesUtcInstant()
    {
        var dto = new DateTimeOffset(2024, 3, 10, 15, 0, 0, TimeSpan.FromHours(2));
        var utc = FilterTimestampTzValueParser.ParseForTimestampTz(dto);
        Assert.Equal(dto.UtcDateTime, utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }
}
