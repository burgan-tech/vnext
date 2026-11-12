using Xunit;

namespace System;

/// <summary>
/// Unit tests for ETagExtensions
/// </summary>
public class ETagExtensionsTests
{
    [Fact]
    public void MatchesIfNoneMatch_ShouldReturnTrue_WhenETagsMatch()
    {
        // Arrange
        var currentETag = "\"abc123\"";
        var ifNoneMatch = "\"abc123\"";

        // Act
        var result = currentETag.MatchesIfNoneMatch(ifNoneMatch);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void MatchesIfNoneMatch_ShouldReturnFalse_WhenETagsDoNotMatch()
    {
        // Arrange
        var currentETag = "\"abc123\"";
        var ifNoneMatch = "\"xyz789\"";

        // Act
        var result = currentETag.MatchesIfNoneMatch(ifNoneMatch);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void MatchesIfNoneMatch_ShouldBeCaseSensitive()
    {
        // Entity-tags compare character by character (RFC 9110 §8.8.3.2); 90338a2e6 made this Ordinal.
        // Arrange
        var currentETag = "\"ABC123\"";
        var ifNoneMatch = "\"abc123\"";

        // Act
        var result = currentETag.MatchesIfNoneMatch(ifNoneMatch);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void MatchesIfNoneMatch_ShouldHandleEmptyStrings()
    {
        // Arrange
        var currentETag = "";
        var ifNoneMatch = "";

        // Act
        var result = currentETag.MatchesIfNoneMatch(ifNoneMatch);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void MatchesIfNoneMatch_ShouldReturnFalse_WhenOneIsEmpty()
    {
        // Arrange
        var currentETag = "\"abc123\"";
        var ifNoneMatch = "";

        // Act
        var result = currentETag.MatchesIfNoneMatch(ifNoneMatch);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void MatchesIfNoneMatch_ShouldHandleWeakETags()
    {
        // Arrange
        var currentETag = "W/\"abc123\"";
        var ifNoneMatch = "W/\"abc123\"";

        // Act
        var result = currentETag.MatchesIfNoneMatch(ifNoneMatch);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void MatchesIfNoneMatch_ShouldHandleSpecialCharacters()
    {
        // Arrange
        var currentETag = "\"abc-123_xyz.456\"";
        var ifNoneMatch = "\"abc-123_xyz.456\"";

        // Act
        var result = currentETag.MatchesIfNoneMatch(ifNoneMatch);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void MatchesIfNoneMatch_ShouldUseOrdinalComparison()
    {
        // Arrange
        var currentETag = "\"AbC123\"";
        var ifNoneMatch = "\"aBc123\"";

        // Act
        var result = currentETag.MatchesIfNoneMatch(ifNoneMatch);

        // Assert
        Assert.False(result);
        Assert.True("\"AbC123\"".MatchesIfNoneMatch("\"AbC123\""));
    }
}

