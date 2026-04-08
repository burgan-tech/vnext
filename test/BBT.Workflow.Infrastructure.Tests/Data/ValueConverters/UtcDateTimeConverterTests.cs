using System;
using BBT.Workflow.Data.ValueConverters;
using Xunit;

namespace BBT.Workflow.Data.ValueConverters;

public class UtcDateTimeConverterTests
{
    private static readonly UtcDateTimeConverter Converter = new();
    private readonly Func<DateTime, DateTime> _read;
    private readonly Func<DateTime, DateTime> _write;

    public UtcDateTimeConverterTests()
    {
        _read = Converter.ConvertFromProviderExpression.Compile();
        _write = Converter.ConvertToProviderExpression.Compile();
    }

    [Fact]
    public void Read_Utc_ReturnsSameValue()
    {
        var input = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var result = _read(input);

        Assert.Equal(input, result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void Read_Local_ConvertsToUtc()
    {
        var localTime = new DateTime(2024, 6, 15, 13, 0, 0, DateTimeKind.Local);

        var result = _read(localTime);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(localTime.ToUniversalTime(), result);
    }

    [Fact]
    public void Read_Unspecified_SpecifiesUtcWithoutShift()
    {
        var unspecified = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Unspecified);

        var result = _read(unspecified);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(unspecified.Ticks, result.Ticks);
    }

    [Fact]
    public void Write_PassesThroughUnchanged()
    {
        var input = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var result = _write(input);

        Assert.Equal(input, result);
    }
}

public class UtcNullableDateTimeConverterTests
{
    private static readonly UtcNullableDateTimeConverter Converter = new();
    private readonly Func<DateTime?, DateTime?> _read;

    public UtcNullableDateTimeConverterTests()
    {
        _read = Converter.ConvertFromProviderExpression.Compile();
    }

    [Fact]
    public void Read_Null_ReturnsNull()
    {
        var result = _read(null);

        Assert.Null(result);
    }

    [Fact]
    public void Read_Utc_ReturnsSameValue()
    {
        var input = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var result = _read(input);

        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
        Assert.Equal(input, result.Value);
    }

    [Fact]
    public void Read_Unspecified_SpecifiesUtcWithoutShift()
    {
        var unspecified = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Unspecified);

        var result = _read(unspecified);

        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
        Assert.Equal(unspecified.Ticks, result.Value.Ticks);
    }
}
