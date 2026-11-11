using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

public sealed class FunctionVerbTests
{
    [Fact]
    public void All_IsExactlyGetPostPatchDelete_InOrder()
    {
        FunctionVerb.All.ShouldBe(new[] { "GET", "POST", "PATCH", "DELETE" });
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("get")]
    [InlineData("Post")]
    [InlineData("POST")]
    [InlineData(" post ")]
    [InlineData("Patch")]
    [InlineData("patch")]
    [InlineData("DELETE")]
    [InlineData("delete")]
    [InlineData("  Delete\t")]
    public void IsKnown_KnownVerb_ReturnsTrue(string verb)
    {
        FunctionVerb.IsKnown(verb).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PUT")]
    [InlineData("QUERY")]
    [InlineData("HEAD")]
    public void IsKnown_UnknownOrBlank_ReturnsFalse(string? verb)
    {
        FunctionVerb.IsKnown(verb).ShouldBeFalse();
    }

    [Fact]
    public void IsKnown_EveryVerbInAll_ReturnsTrue()
    {
        foreach (var verb in FunctionVerb.All)
        {
            FunctionVerb.IsKnown(verb).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(" get ", "GET")]
    [InlineData("Post", "POST")]
    [InlineData("\tpatch\n", "PATCH")]
    [InlineData("DELETE", "DELETE")]
    public void Normalize_TrimsAndUpperCases(string input, string expected)
    {
        FunctionVerb.Normalize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("post")]
    [InlineData("Patch")]
    [InlineData(" patch ")]
    [InlineData("DELETE")]
    [InlineData("delete")]
    public void CarriesBody_BodyVerb_ReturnsTrue(string verb)
    {
        FunctionVerb.CarriesBody(verb).ShouldBeTrue();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("get")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PUT")]
    [InlineData("QUERY")]
    public void CarriesBody_NoBodyOrUnknown_ReturnsFalse(string? verb)
    {
        FunctionVerb.CarriesBody(verb).ShouldBeFalse();
    }
}
