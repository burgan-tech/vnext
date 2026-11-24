using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Shared.Json;

/// <summary>
/// <see cref="JsonPath.WithoutFirstSegment"/>: the part of an input-phase <c>select</c> read inside its variable.
/// </summary>
public sealed class JsonPathTailTests
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
}
