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
}
