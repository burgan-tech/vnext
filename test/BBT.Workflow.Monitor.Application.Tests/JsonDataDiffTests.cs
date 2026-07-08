using System.Text.Json;
using BBT.Workflow.Monitor.Instances;
using Xunit;

namespace BBT.Workflow.Monitor.Application.Tests;

public sealed class JsonDataDiffTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Compare_DetectsAddedRemovedChangedAndUnchanged()
    {
        var from = Parse("""{"a":1,"b":2,"keep":"x"}""");
        var to   = Parse("""{"b":3,"c":4,"keep":"x"}""");

        var diff = JsonDataDiff.Compare(from, to);

        Assert.Contains(diff.Added,   e => e.Path == "c" && e.Value == "4");
        Assert.Contains(diff.Removed, e => e.Path == "a" && e.Value == "1");
        Assert.Contains(diff.Changed, e => e.Path == "b" && e.OldValue == "2" && e.NewValue == "3");
        Assert.Equal(1, diff.UnchangedCount);
    }

    [Fact]
    public void Compare_NestedObjects_UsesDottedPaths()
    {
        var from = Parse("""{"payment":{"amount":100}}""");
        var to   = Parse("""{"payment":{"amount":250}}""");

        var diff = JsonDataDiff.Compare(from, to);

        Assert.Contains(diff.Changed, e => e.Path == "payment.amount" && e.OldValue == "100" && e.NewValue == "250");
    }
}
