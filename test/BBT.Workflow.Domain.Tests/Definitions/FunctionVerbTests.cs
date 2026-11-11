using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

public sealed class FunctionVerbTests
{
    [Fact]
    public void All_IsExactlyGetPostPatchDelete_InOrder()
    {
        FunctionVerb.All.ShouldBe(["GET", "POST", "PATCH", "DELETE"]);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("get")]
    [InlineData("post")]
    [InlineData("Patch")]
    [InlineData("delete")]
    [InlineData(" post ")]
    [InlineData("  GET\t")]
    public void IsKnown_ReturnsTrue_ForKnownVerbs(string verb)
    {
        FunctionVerb.IsKnown(verb).ShouldBeTrue();
    }

    [Fact]
    public void IsKnown_ReturnsTrue_ForEveryVerbInAll()
    {
        foreach (var verb in FunctionVerb.All)
            FunctionVerb.IsKnown(verb).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PUT")]
    [InlineData("QUERY")]
    [InlineData("HEAD")]
    public void IsKnown_ReturnsFalse_ForUnknownOrBlank(string? verb)
    {
        FunctionVerb.IsKnown(verb).ShouldBeFalse();
    }

    [Theory]
    [InlineData(" get ", "GET")]
    [InlineData("Patch", "PATCH")]
    [InlineData("  dElEtE\t", "DELETE")]
    [InlineData("POST", "POST")]
    public void Normalize_TrimsAndUpperCases(string verb, string expected)
    {
        FunctionVerb.Normalize(verb).ShouldBe(expected);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("post")]
    [InlineData("Patch")]
    [InlineData("delete")]
    public void CarriesBody_ReturnsTrue_ForBodyVerbs(string verb)
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
    public void CarriesBody_ReturnsFalse_ForGetNullEmptyAndUnknown(string? verb)
    {
        FunctionVerb.CarriesBody(verb).ShouldBeFalse();
    }
}
