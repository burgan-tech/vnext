using BBT.Workflow.Monitor.Instances;
using Xunit;

namespace BBT.Workflow.Monitor.Application.Tests;

public sealed class FaultedFilterValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildEffectiveFilter_MissingFilter_ReturnsFilterRequired(string? filter)
    {
        var (result, effective) = FaultedFilterValidator.BuildEffectiveFilter(filter);

        Assert.Equal(FaultedFilterValidation.FilterRequired, result);
        Assert.Null(effective);
    }

    [Fact]
    public void BuildEffectiveFilter_InvalidJson_ReturnsFilterInvalid()
    {
        var (result, effective) = FaultedFilterValidator.BuildEffectiveFilter("{not json");

        Assert.Equal(FaultedFilterValidation.FilterInvalid, result);
        Assert.Null(effective);
    }

    [Fact]
    public void BuildEffectiveFilter_StatusSupplied_ReturnsStatusNotAllowed()
    {
        var (result, _) = FaultedFilterValidator.BuildEffectiveFilter(
            """{"and":[{"createdAt":{"gt":"2026-06-01T00:00:00Z","lt":"2026-06-27T00:00:00Z"}},{"status":{"eq":"Active"}}]}""");

        Assert.Equal(FaultedFilterValidation.StatusNotAllowed, result);
    }

    [Fact]
    public void BuildEffectiveFilter_CreatedAtOnlyLowerBound_ReturnsCreatedAtRangeRequired()
    {
        var (result, _) = FaultedFilterValidator.BuildEffectiveFilter("""{"createdAt":{"gt":"2026-06-01T00:00:00Z"}}""");

        Assert.Equal(FaultedFilterValidation.CreatedAtRangeRequired, result);
    }

    [Fact]
    public void BuildEffectiveFilter_NoCreatedAt_ReturnsCreatedAtRangeRequired()
    {
        var (result, _) = FaultedFilterValidator.BuildEffectiveFilter("""{"attributes":{"amount":{"gt":100}}}""");

        Assert.Equal(FaultedFilterValidation.CreatedAtRangeRequired, result);
    }

    [Fact]
    public void BuildEffectiveFilter_CreatedAtInOrBranch_NotCountedAsBounded()
    {
        var (result, _) = FaultedFilterValidator.BuildEffectiveFilter(
            """{"or":[{"createdAt":{"gt":"2026-06-01T00:00:00Z"}},{"createdAt":{"lt":"2026-06-27T00:00:00Z"}}]}""");

        Assert.Equal(FaultedFilterValidation.CreatedAtRangeRequired, result);
    }

    [Fact]
    public void BuildEffectiveFilter_BoundedSingleField_ReturnsValidWithInjectedStatus()
    {
        var (result, effective) = FaultedFilterValidator.BuildEffectiveFilter(
            """{"createdAt":{"gt":"2026-06-01T00:00:00Z","lt":"2026-06-27T00:00:00Z"}}""");

        Assert.Equal(FaultedFilterValidation.Valid, result);
        Assert.NotNull(effective);
        Assert.Contains("\"and\"", effective);
        Assert.Contains("\"status\"", effective);
        Assert.Contains("Faulted", effective);
        Assert.Contains("createdAt", effective);
    }

    [Fact]
    public void BuildEffectiveFilter_BoundedAcrossAndBranches_ReturnsValid()
    {
        var (result, _) = FaultedFilterValidator.BuildEffectiveFilter(
            """{"and":[{"createdAt":{"ge":"2026-06-01T00:00:00Z"}},{"createdAt":{"le":"2026-06-27T00:00:00Z"}}]}""");

        Assert.Equal(FaultedFilterValidation.Valid, result);
    }

    [Fact]
    public void BuildEffectiveFilter_Between_ReturnsValid()
    {
        var (result, _) = FaultedFilterValidator.BuildEffectiveFilter(
            """{"createdAt":{"between":["2026-06-01T00:00:00Z","2026-06-27T00:00:00Z"]}}""");

        Assert.Equal(FaultedFilterValidation.Valid, result);
    }

    [Fact]
    public void BuildEffectiveFilter_BoundedWithExtraBusinessFilter_ReturnsValid()
    {
        var (result, _) = FaultedFilterValidator.BuildEffectiveFilter(
            """{"and":[{"createdAt":{"gt":"2026-06-01T00:00:00Z","lt":"2026-06-27T00:00:00Z"}},{"attributes":{"amount":{"gt":100}}}]}""");

        Assert.Equal(FaultedFilterValidation.Valid, result);
    }

    [Fact]
    public void BuildEffectiveFilter_EffectiveFilterReparsesCleanly()
    {
        var (_, effective) = FaultedFilterValidator.BuildEffectiveFilter(
            """{"createdAt":{"between":["2026-06-01T00:00:00Z","2026-06-27T00:00:00Z"]}}""");

        // The effective filter must be valid GraphQL JSON the existing parser accepts.
        Assert.NotNull(effective);
        var node = BBT.Workflow.Definitions.GraphQL.GraphQLFilterParser.ParseFilter(effective);
        Assert.NotNull(node);
        Assert.Equal(BBT.Workflow.Definitions.GraphQL.FilterNodeType.And, node!.NodeType);
    }

    [Fact]
    public void BuildEffectiveFilter_StatusInsideNot_ReturnsStatusNotAllowed()
    {
        var (result, _) = FaultedFilterValidator.BuildEffectiveFilter(
            """{"and":[{"createdAt":{"gt":"2026-06-01T00:00:00Z","lt":"2026-06-27T00:00:00Z"}},{"not":{"status":{"eq":"Completed"}}}]}""");

        Assert.Equal(FaultedFilterValidation.StatusNotAllowed, result);
    }

    [Fact]
    public void BuildEffectiveFilter_FlatStatusAndCreatedAt_ReturnsStatusNotAllowed()
    {
        var (result, _) = FaultedFilterValidator.BuildEffectiveFilter(
            """{"status":{"eq":"Active"},"createdAt":{"gt":"2026-06-01T00:00:00Z","lt":"2026-06-27T00:00:00Z"}}""");

        Assert.Equal(FaultedFilterValidation.StatusNotAllowed, result);
    }
}
