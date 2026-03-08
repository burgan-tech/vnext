using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for View.GetContentAsTyped() (Issue #429 – view content typed by ViewType).
/// </summary>
public class ViewGetContentAsTypedTests
{
    private static View CreateView(ViewType type, string content)
    {
        var json = $$"""
            {"type": {{(int)type}}, "content": "{{content.Replace("\"", "\\\"")}}", "display": "test"}
            """;
        var view = JsonSerializer.Deserialize<View>(json, JsonSerializerConstants.JsonOptions);
        view.ShouldNotBeNull();
        return view;
    }

    [Fact]
    public void GetContentAsTyped_Html_ReturnsContentAsString()
    {
        var view = CreateView(ViewType.Html, "<p>hello</p>");
        var result = view.GetContentAsTyped();
        result.ShouldBeOfType<string>();
        result.ShouldBe("<p>hello</p>");
    }

    [Fact]
    public void GetContentAsTyped_Markdown_ReturnsContentAsString()
    {
        var view = CreateView(ViewType.Markdown, "# Title");
        var result = view.GetContentAsTyped();
        result.ShouldBeOfType<string>();
        result.ShouldBe("# Title");
    }

    [Fact]
    public void GetContentAsTyped_Json_WithValidJson_ReturnsJsonElement()
    {
        var view = CreateView(ViewType.Json, """{"a":1,"b":"x"}""");
        var result = view.GetContentAsTyped();
        result.ShouldBeOfType<JsonElement>();
        var el = (JsonElement)result;
        el.GetProperty("a").GetInt32().ShouldBe(1);
        el.GetProperty("b").GetString().ShouldBe("x");
    }

    [Fact]
    public void GetContentAsTyped_Json_WithInvalidJson_ReturnsOriginalString_NoException()
    {
        var view = CreateView(ViewType.Json, "not valid json");
        var result = view.GetContentAsTyped();
        result.ShouldBeOfType<string>();
        result.ShouldBe("not valid json");
    }

    [Fact]
    public void GetContentAsTyped_DeepLink_WithValidJson_ReturnsJsonElement()
    {
        var view = CreateView(ViewType.DeepLink, """{"url":"myapp://path","scheme":"myapp"}""");
        var result = view.GetContentAsTyped();
        result.ShouldBeOfType<JsonElement>();
        var el = (JsonElement)result;
        el.GetProperty("url").GetString().ShouldBe("myapp://path");
    }

    [Fact]
    public void GetContentAsTyped_DeepLink_WithInvalidJson_ReturnsOriginalString_NoException()
    {
        var view = CreateView(ViewType.DeepLink, "not json");
        var result = view.GetContentAsTyped();
        result.ShouldBeOfType<string>();
        result.ShouldBe("not json");
    }

    [Fact]
    public void GetContentAsTyped_Http_WithValidJson_ReturnsJsonElement()
    {
        var view = CreateView(ViewType.Http, """{"method":"GET","url":"https://example.com"}""");
        var result = view.GetContentAsTyped();
        result.ShouldBeOfType<JsonElement>();
        var el = (JsonElement)result;
        el.GetProperty("method").GetString().ShouldBe("GET");
    }

    [Fact]
    public void GetContentAsTyped_URN_WithValidJson_ReturnsJsonElement()
    {
        var view = CreateView(ViewType.URN, """{"urn":"urn:example:1"}""");
        var result = view.GetContentAsTyped();
        result.ShouldBeOfType<JsonElement>();
        var el = (JsonElement)result;
        el.GetProperty("urn").GetString().ShouldBe("urn:example:1");
    }

    [Fact]
    public void GetContentAsTyped_Json_WithValidJsonArray_ReturnsJsonElement()
    {
        var view = CreateView(ViewType.Json, "[1,2,3]");
        var result = view.GetContentAsTyped();
        result.ShouldBeOfType<JsonElement>();
        var el = (JsonElement)result;
        el.GetArrayLength().ShouldBe(3);
        el[0].GetInt32().ShouldBe(1);
    }
}
