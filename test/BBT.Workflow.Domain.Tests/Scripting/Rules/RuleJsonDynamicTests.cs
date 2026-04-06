using System.Text.Json;
using BBT.Workflow.Scripting.Rules;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Scripting.Rules;

public sealed class RuleJsonDynamicTests
{
    [Fact]
    public void StringIndexer_WhenKeyMissing_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"a":1}""");
        dynamic root = RuleJsonDynamic.FromJsonElement(doc.RootElement);
        object? v = root["missing"];
        v.ShouldBeNull();
    }

    [Fact]
    public void MemberAccess_WhenPropertyMissing_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"a":1}""");
        dynamic root = RuleJsonDynamic.FromJsonElement(doc.RootElement);
        object? v = root.missing;
        v.ShouldBeNull();
    }

    [Fact]
    public void StringIndexer_WhenValueIsJsonNull_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"a":null}""");
        dynamic root = RuleJsonDynamic.FromJsonElement(doc.RootElement);
        object? v = root["a"];
        v.ShouldBeNull();
    }

    [Fact]
    public void ToString_WhenStringProperty_ReturnsUnquotedText()
    {
        using var doc = JsonDocument.Parse("""{"absenceType":"personal-leave"}""");
        var leaf = RuleJsonDynamic.FromJsonElement(doc.RootElement.GetProperty("absenceType"));
        leaf.ToString().ShouldBe("personal-leave");
    }

    [Fact]
    public void ToString_WhenObjectRoot_ReturnsCompactJson()
    {
        using var doc = JsonDocument.Parse("""{"x":1}""");
        var root = RuleJsonDynamic.FromJsonElement(doc.RootElement);
        root.ToString().ShouldBe("""{"x":1}""");
    }

    [Fact]
    public void ToString_WhenNumber_ReturnsRawJsonNumber()
    {
        using var doc = JsonDocument.Parse("42");
        var n = RuleJsonDynamic.FromJsonElement(doc.RootElement);
        n.ToString().ShouldBe("42");
    }
}
