using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// The composed data-function ETag of an instance with an active SubFlow (#1084):
/// <c>{parentEtag}.{childEtag}</c>, split at the first separator so a grandchild's ETag rides along.
/// </summary>
public class SubFlowDataEtagTests
{
    [Fact]
    public void Compose_StripsTheChildsQuotes() =>
        SubFlowDataEtag.Compose("p1", "\"c1\"").ShouldBe("p1.c1");

    [Theory]
    [InlineData("\"p1.c1\"")]
    [InlineData("p1.c1")]
    [InlineData("W/\"p1.c1\"")]
    public void ChildPartFor_ParentPartMatches_ReturnsChildPart(string ifNoneMatch) =>
        SubFlowDataEtag.ChildPartFor(ifNoneMatch, "p1").ShouldBe("c1");

    [Fact]
    public void ChildPartFor_KeepsTheGrandchildPartForTheChild() =>
        SubFlowDataEtag.ChildPartFor("\"p1.c1.g1\"", "p1").ShouldBe("c1.g1");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"p1\"")]        // parent-only ETag (no SubFlow when it was issued)
    [InlineData("\"p2.c1\"")]     // parent moved
    [InlineData("\"p1.\"")]       // empty child part
    [InlineData("\".c1\"")]       // empty parent part
    public void ChildPartFor_NoUsableChildPart_ReturnsNull(string? ifNoneMatch) =>
        SubFlowDataEtag.ChildPartFor(ifNoneMatch, "p1").ShouldBeNull();
}
