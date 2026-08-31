using System.Text.Json;
using BBT.Workflow;
using Xunit;

namespace BBT.Workflow.Shared;

public class JsonDataMemoTests
{
    [Fact]
    public void JsonElement_IsMemoized_EqualAcrossAccesses()
    {
        var data = new JsonData("""{"a":1,"b":{"c":"x"}}""");
        var first = data.JsonElement;
        var second = data.JsonElement;
        // JsonElement bir struct — referans değil, memo'lanmış aynı belge üzerinden eşdeğerlik pinlenir:
        Assert.True(JsonElement.DeepEquals(first, second));
        Assert.Equal(first.GetRawText(), second.GetRawText());
    }

    [Fact]
    public void JsonElement_Memo_DoesNotAffectValueEquality()
    {
        var x = new JsonData("""{"a":1}""");
        var y = new JsonData("""{"a":1}""");
        _ = x.JsonElement;
        Assert.True(x.ValueEquals(y));
    }

    [Fact]
    public void FromElement_DetachesFromCallerOwnedDocument()
    {
        JsonData data;
        using (var document = JsonDocument.Parse("""{"value":42}"""))
        {
            data = JsonData.FromElement(document.RootElement);
        }

        Assert.Equal(42, data.JsonElement.GetProperty("value").GetInt32());
        Assert.Equal("""{"value":42}""", data.Json);
    }

    [Fact]
    public void FromMaterializedObject_UsesRequestedSerializerOptions_AndSeedsElementMemo()
    {
        var payload = new MaterializationProbe("Ada", 7);
        var data = JsonData.FromMaterializedObject(
            payload,
            JsonSerializerConstants.JsonOptions);

        var first = data.JsonElement;
        var second = data.JsonElement;

        Assert.Equal("Ada", first.GetProperty("displayName").GetString());
        Assert.Equal(7, first.GetProperty("score").GetInt32());
        Assert.Equal(
            JsonSerializer.Serialize(payload, JsonSerializerConstants.JsonOptions),
            data.Json);
        Assert.Equal(data.Json, first.GetRawText());
        Assert.True(JsonElement.DeepEquals(first, second));
    }

    private sealed record MaterializationProbe(string DisplayName, int Score);
}
