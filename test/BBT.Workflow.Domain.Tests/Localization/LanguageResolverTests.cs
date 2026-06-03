using System.Collections.Generic;
using Xunit;

namespace BBT.Workflow;

public class LanguageResolverTests
{
    [Theory]
    [InlineData("tr-TR,tr;q=0.9,en-US;q=0.8", "tr-TR")]
    [InlineData("tr", "tr")]
    [InlineData("en-US", "en-US")]
    [InlineData(" fr-FR ; q=1.0 , en ", "fr-FR")]
    public void ResolveCulture_FromHeaderValue_ReturnsFirstLanguage(string headerValue, string expected)
    {
        Assert.Equal(expected, LanguageResolver.ResolveCulture(headerValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveCulture_FromHeaderValue_DefaultsToEnUs_WhenEmpty(string? headerValue)
    {
        Assert.Equal("en-US", LanguageResolver.ResolveCulture(headerValue));
    }

    [Fact]
    public void ResolveCulture_FromHeaders_ReadsAcceptLanguage_CaseInsensitive()
    {
        var headers = new Dictionary<string, string?>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["accept-language"] = "tr-TR,tr;q=0.9"
        };

        Assert.Equal("tr-TR", LanguageResolver.ResolveCulture(headers));
    }

    [Fact]
    public void ResolveCulture_FromHeaders_DefaultsToEnUs_WhenMissingOrNull()
    {
        Assert.Equal("en-US", LanguageResolver.ResolveCulture((IReadOnlyDictionary<string, string?>?)null));
        Assert.Equal("en-US", LanguageResolver.ResolveCulture(new Dictionary<string, string?>()));
    }
}
