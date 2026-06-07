using System.Collections.Generic;
using Xunit;

namespace BBT.Workflow;

public class LanguageLabelExtensionsTests
{
    private static List<LanguageLabel> Labels(params (string label, string language)[] items)
    {
        var list = new List<LanguageLabel>();
        foreach (var (label, language) in items)
            list.Add(new LanguageLabel(label, language));
        return list;
    }

    [Fact]
    public void ResolveLabel_ReturnsExactCultureMatch()
    {
        var labels = Labels(("Türkçe", "tr-TR"), ("English", "en"));
        Assert.Equal("Türkçe", labels.ResolveLabel("tr-TR"));
    }

    [Fact]
    public void ResolveLabel_FallsBackToNeutralLanguage()
    {
        var labels = Labels(("Türkçe", "tr"), ("English", "en"));
        // requested tr-TR has no exact match → neutral "tr"
        Assert.Equal("Türkçe", labels.ResolveLabel("tr-TR"));
    }

    [Fact]
    public void ResolveLabel_FallsBackToRegionalVariantOfNeutral()
    {
        var labels = Labels(("Deutsch", "de-DE"), ("English", "en"));
        // requested "de" has no exact "de", but matches regional "de-DE"
        Assert.Equal("Deutsch", labels.ResolveLabel("de"));
    }

    [Fact]
    public void ResolveLabel_FallsBackToEnglish_WhenNoLanguageMatch()
    {
        var labels = Labels(("Deutsch", "de"), ("English", "en-US"));
        Assert.Equal("English", labels.ResolveLabel("fr-FR"));
    }

    [Fact]
    public void ResolveLabel_FallsBackToFirstItem_WhenNoMatchAndNoEnglish()
    {
        var labels = Labels(("Deutsch", "de"), ("Español", "es"));
        Assert.Equal("Deutsch", labels.ResolveLabel("fr-FR"));
    }

    [Fact]
    public void ResolveLabel_ReturnsNull_WhenNullOrEmpty()
    {
        Assert.Null(((IEnumerable<LanguageLabel>?)null).ResolveLabel("tr-TR"));
        Assert.Null(new List<LanguageLabel>().ResolveLabel("tr-TR"));
    }
}
