using System;
using BBT.Workflow.Definitions.GraphQL;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

/// <summary>
/// Unit tests for FilterFormatDetector
/// </summary>
public class FilterFormatDetectorTests : DomainTestBase<DomainEntryPoint>
{
    #region DetectFormat Single Filter Tests

    [Fact]
    public void DetectFormat_ShouldReturnEmpty_WhenNullInput()
    {
        // Act
        var result = FilterFormatDetector.DetectFormat((string?)null);

        // Assert
        Assert.Equal(FilterFormat.Empty, result);
    }

    [Fact]
    public void DetectFormat_ShouldReturnEmpty_WhenEmptyInput()
    {
        // Act
        var result = FilterFormatDetector.DetectFormat("");

        // Assert
        Assert.Equal(FilterFormat.Empty, result);
    }

    [Fact]
    public void DetectFormat_ShouldReturnEmpty_WhenWhitespaceInput()
    {
        // Act
        var result = FilterFormatDetector.DetectFormat("   ");

        // Assert
        Assert.Equal(FilterFormat.Empty, result);
    }

    [Theory]
    [InlineData("""{"attributes":{"clientId":{"eq":122}}}""")]
    [InlineData("""{"and":[{"attributes":{"status":{"eq":"active"}}}]}""")]
    [InlineData("""{"or":[{"attributes":{"a":{"eq":"1"}}},{"attributes":{"b":{"eq":"2"}}}]}""")]
    [InlineData("""{"not":{"attributes":{"status":{"eq":"deleted"}}}}""")]
    public void DetectFormat_ShouldReturnGraphQL_ForJsonFilters(string filter)
    {
        // Act
        var result = FilterFormatDetector.DetectFormat(filter);

        // Assert
        Assert.Equal(FilterFormat.GraphQL, result);
    }

    [Theory]
    [InlineData("attributes=clientId=eq:122")]
    [InlineData("name=eq:John")]
    [InlineData("age=gt:25")]
    [InlineData("code=sgt:M")]
    [InlineData("startedAt=dgt:2024-01-01")]
    [InlineData("status=in:active,pending")]
    [InlineData("amount=between:100,500")]
    [InlineData("email=startswith:test@")]
    [InlineData("domain=endswith:.com")]
    [InlineData("description=like:keyword")]
    public void DetectFormat_ShouldReturnLegacy_ForLegacyFilters(string filter)
    {
        // Act
        var result = FilterFormatDetector.DetectFormat(filter);

        // Assert
        Assert.Equal(FilterFormat.Legacy, result);
    }

    #endregion

    #region IsGraphQLFormat Tests

    [Fact]
    public void IsGraphQLFormat_ShouldReturnTrue_ForJsonFilter()
    {
        // Arrange
        var filter = """{"attributes":{"clientId":{"eq":122}}}""";

        // Act
        var result = FilterFormatDetector.IsGraphQLFormat(filter);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsGraphQLFormat_ShouldReturnFalse_ForLegacyFilter()
    {
        // Arrange
        var filter = "clientId=eq:122";

        // Act
        var result = FilterFormatDetector.IsGraphQLFormat(filter);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region IsLegacyFormat Tests

    [Fact]
    public void IsLegacyFormat_ShouldReturnTrue_ForLegacyFilter()
    {
        // Arrange
        var filter = "clientId=eq:122";

        // Act
        var result = FilterFormatDetector.IsLegacyFormat(filter);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsLegacyFormat_ShouldReturnFalse_ForJsonFilter()
    {
        // Arrange
        var filter = """{"attributes":{"clientId":{"eq":122}}}""";

        // Act
        var result = FilterFormatDetector.IsLegacyFormat(filter);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region CombineFilters Tests

    [Fact]
    public void CombineFilters_ShouldReturnNull_WhenNullInput()
    {
        var result = FilterFormatDetector.CombineFilters(null);
        Assert.Null(result);
    }

    [Fact]
    public void CombineFilters_ShouldReturnNull_WhenEmptyInput()
    {
        var result = FilterFormatDetector.CombineFilters("");
        Assert.Null(result);
    }

    [Fact]
    public void CombineFilters_ShouldReturnSingleNode_WhenOneFilter()
    {
        var filter = """{"attributes":{"clientId":{"eq":122}}}""";
        var result = FilterFormatDetector.CombineFilters(filter);
        Assert.NotNull(result);
        Assert.Equal(FilterNodeType.Condition, result.NodeType);
    }

    #endregion

    #region ConvertLegacyToGraphQL Tests

    [Fact]
    public void ConvertLegacyToGraphQL_ShouldReturnNull_WhenNullInput()
    {
        var result = FilterFormatDetector.ConvertLegacyToGraphQL(null);
        Assert.Null(result);
    }

    [Fact]
    public void ConvertLegacyToGraphQL_ShouldReturnNull_WhenEmptyInput()
    {
        var result = FilterFormatDetector.ConvertLegacyToGraphQL("");
        Assert.Null(result);
    }

    [Fact]
    public void ConvertLegacyToGraphQL_ShouldConvertSingleFilter()
    {
        var filter = "attributes=clientId=eq:122";
        var result = FilterFormatDetector.ConvertLegacyToGraphQL(filter);
        Assert.NotNull(result);
        Assert.Equal(FilterNodeType.Condition, result.NodeType);
        Assert.NotNull(result.Attributes);
        Assert.True(result.Attributes.ContainsKey("clientId"));
    }

    [Fact]
    public void ConvertLegacyToGraphQL_ShouldHandleNumericValues()
    {
        var filter = "amount=gt:100";
        var result = FilterFormatDetector.ConvertLegacyToGraphQL(filter);
        Assert.NotNull(result);
        Assert.NotNull(result.Attributes);
        Assert.NotNull(result.Attributes["amount"].Gt);
    }

    [Fact]
    public void ConvertLegacyToGraphQL_ShouldReturnNull_WhenInvalidFilter()
    {
        var result = FilterFormatDetector.ConvertLegacyToGraphQL("invalid-filter-format");
        Assert.Null(result);
    }

    #endregion
}




