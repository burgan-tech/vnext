using BBT.Workflow.Execution.Bindings;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Bindings;

public sealed class HttpContentTypeTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("application/json", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("application/x-www-form-urlencoded", false)]
    [InlineData("text/plain", false)]
    [InlineData("text/xml", false)]
    public void IsJson_ClassifiesContentType(string? contentType, bool expected)
    {
        HttpContentType.IsJson(contentType).ShouldBe(expected);
    }

    [Fact]
    public void Resolve_ExplicitContentType_Wins()
    {
        HttpContentType.Resolve("text/plain", "application/x-www-form-urlencoded")
            .ShouldBe("text/plain");
    }

    [Fact]
    public void Resolve_NoExplicit_FallsBackToHeader()
    {
        HttpContentType.Resolve(null, "application/x-www-form-urlencoded")
            .ShouldBe("application/x-www-form-urlencoded");
    }

    [Fact]
    public void Resolve_Nothing_DefaultsToApplicationJson()
    {
        HttpContentType.Resolve(null, null).ShouldBe("application/json");
        HttpContentType.Resolve("  ", "  ").ShouldBe("application/json");
    }
}
