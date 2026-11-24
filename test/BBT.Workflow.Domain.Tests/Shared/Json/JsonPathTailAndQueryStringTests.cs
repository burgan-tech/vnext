using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Shared.Json;

/// <summary>
/// <see cref="JsonPath.WithoutFirstSegment"/> (the part of an input-phase <c>select</c> read inside its variable) and
/// <see cref="TaskQueryString"/> (the <see cref="IHasQuery"/> surface of a task's query-string text).
/// </summary>
public sealed class JsonPathTailAndQueryStringTests
{
    [Theory]
    [InlineData("$.data.customer.id", "$['customer']['id']")]
    [InlineData("$.data", "$")]
    [InlineData("$", "$")]
    [InlineData("$.data['a.b'].items[1]", "$['a.b']['items'][1]")]
    [InlineData("$.data['it\\'s'][*]", "$['it\\'s'][*]")]
    public void WithoutFirstSegment_IsRootedAndRoundTrips(string path, string expected)
    {
        var tail = JsonPath.Parse(path).Value!.WithoutFirstSegment();

        tail.Text.ShouldBe(expected);
        JsonPath.Parse(tail.Text).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void WithoutFirstSegment_ReadsInsideTheVariable()
    {
        using var document = JsonDocument.Parse("""{"customer":{"id":7}}""");

        JsonPath.Parse("$.data.customer.id").Value!.WithoutFirstSegment()
            .TryRead(document.RootElement, out var value).ShouldBeTrue();

        value.GetInt32().ShouldBe(7);
    }

    [Fact]
    public void TaskQueryString_ParsesAndFormats_PercentEncoded_InOrder()
    {
        var pairs = TaskQueryString.Parse("?size=10&q=a%20b&flag");

        pairs.ShouldBe([
            new KeyValuePair<string, string?>("size", "10"),
            new KeyValuePair<string, string?>("q", "a b"),
            new KeyValuePair<string, string?>("flag", null)
        ]);
        TaskQueryString.Format(pairs).ShouldBe("size=10&q=a%20b&flag");
        TaskQueryString.Parse(null).ShouldBeEmpty();
        TaskQueryString.Format([]).ShouldBeNull();
    }
}
