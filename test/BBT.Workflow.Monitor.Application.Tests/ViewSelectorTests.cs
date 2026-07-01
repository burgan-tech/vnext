using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitor.Instances;
using Xunit;

namespace BBT.Workflow.Monitor.Application.Tests;

public class ViewSelectorTests
{
    private static ViewEntry RulelessEntry(string key)
        => ViewEntry.CreateDefault(new Reference(key, "test-domain", "sys-views", "1.0.0"));

    private static ViewEntry RuledEntry(string key)
        => ViewEntry.CreateWithRule(
            new Reference(key, "test-domain", "sys-views", "1.0.0"),
            new ScriptCode("loc", "return true;"));

    [Fact]
    public void Select_Null_ReturnsNoDefaultAndEmptyCandidates()
    {
        var result = ViewSelector.Select(null);
        Assert.Null(result.Default);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Select_SingleRulelessEntry_PicksItAsDefault()
    {
        var def = ViewDefinition.CreateWithViews(RulelessEntry("v1"));
        var result = ViewSelector.Select(def);
        Assert.Equal("v1", result.Default!.View.Key);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void Select_RuleEntriesFirst_PrefersFirstRulelessAsDefault()
    {
        var def = ViewDefinition.CreateWithViews(RuledEntry("ruled"), RulelessEntry("plain"));
        var result = ViewSelector.Select(def);
        Assert.Equal("plain", result.Default!.View.Key);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Select_AllRuled_FallsBackToFirstAsDefault()
    {
        var def = ViewDefinition.CreateWithViews(RuledEntry("a"), RuledEntry("b"));
        var result = ViewSelector.Select(def);
        Assert.Equal("a", result.Default!.View.Key);
    }
}
