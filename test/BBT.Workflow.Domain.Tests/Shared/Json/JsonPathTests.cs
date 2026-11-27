using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Shared.Json;

public sealed class JsonPathTests
{
    private static JsonPath P(string text)
    {
        var result = JsonPath.Parse(text);
        result.IsSuccess.ShouldBeTrue(text);
        return result.Value!;
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$.a.b")]
    [InlineData("$['x-y'].c")]
    [InlineData("$.a[0]")]
    [InlineData("$.a[*].b")]
    [InlineData("$.headers.x-channel")]
    [InlineData("$['a.b']")]
    [InlineData("$['it\\'s']")]
    public void Parse_Subset_Accepted(string text) => JsonPath.Parse(text).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("$..a")]
    [InlineData("$.a[?(@.b)]")]
    [InlineData("a.b")]
    [InlineData("")]
    [InlineData("$.")]
    [InlineData("$.a..b")]
    [InlineData("$.a[")]
    [InlineData("$.a['x]")]
    [InlineData("$.a[]")]
    [InlineData("$.a[-1]")]
    [InlineData("$.a b")]
    [InlineData("$a")]
    public void Parse_FilterOrRecursiveOrMalformed_Rejected(string text)
        => JsonPath.Parse(text).IsSuccess.ShouldBeFalse(text);

    [Fact]
    public void Parse_Null_Rejected() => JsonPath.Parse(null!).IsSuccess.ShouldBeFalse();

    [Fact]
    public void FirstSegment_IsVariableName()
    {
        P("$.order.id").FirstSegment.ShouldBe("order");
        P("$['x-y'].c").FirstSegment.ShouldBe("x-y");
        P("$").FirstSegment.ShouldBe("");
    }

    [Theory]
    [InlineData("$.a.b", true)]
    [InlineData("$['x.y'].c", true)]
    [InlineData("$.a[0]", false)]
    [InlineData("$.a[*].b", false)]
    [InlineData("$", true)]
    public void IsWritable_FalseForIndexAndWildcard(string text, bool expected)
        => P(text).IsWritable.ShouldBe(expected);

    [Fact]
    public void Set_CreatesIntermediateObjects()
    {
        var root = new JsonObject();
        P("$.a.b.c").Set(root, JsonValue.Create(5));
        root.ToJsonString().ShouldBe("""{"a":{"b":{"c":5}}}""");
    }

    [Fact]
    public void Set_BracketNameWithDot_IsSingleProperty()
    {
        var root = new JsonObject();
        P("$['a.b']").Set(root, JsonValue.Create("v"));
        root.ToJsonString().ShouldBe("""{"a.b":"v"}""");
    }

    [Fact]
    public void Set_ReplacesExistingProperty_ArraysWhole()
    {
        var root = JsonNode.Parse("""{"a":{"b":[1,2,3],"k":1}}""")!.AsObject();
        P("$.a.b").Set(root, new JsonArray(9));
        root.ToJsonString().ShouldBe("""{"a":{"b":[9],"k":1}}""");
    }

    [Fact]
    public void Set_IntoNonObjectIntermediate_Fails()
    {
        var root = JsonNode.Parse("""{"a":5}""")!.AsObject();
        Should.Throw<InvalidOperationException>(() => P("$.a.b").Set(root, JsonValue.Create(1)));
        var arr = JsonNode.Parse("""{"a":[1]}""")!.AsObject();
        Should.Throw<InvalidOperationException>(() => P("$.a.b").Set(arr, JsonValue.Create(1)));
    }

    [Fact]
    public void Set_NonWritablePath_Throws()
    {
        Should.Throw<InvalidOperationException>(() => P("$.a[0]").Set(new JsonObject(), JsonValue.Create(1)));
        Should.Throw<InvalidOperationException>(() => P("$.a[*].b").Set(new JsonObject(), JsonValue.Create(1)));
    }

    [Fact]
    public void Set_Root_Throws()
        => Should.Throw<InvalidOperationException>(() => P("$").Set(new JsonObject(), JsonValue.Create(1)));

    [Fact]
    public void Set_NodeWithParent_IsDeepCloned()
    {
        var source = JsonNode.Parse("""{"x":{"y":1}}""")!.AsObject();
        var child = source["x"]!; // has a parent
        var root = new JsonObject();
        P("$.copy").Set(root, child);
        root.ToJsonString().ShouldBe("""{"copy":{"y":1}}""");
        source.ToJsonString().ShouldBe("""{"x":{"y":1}}""");
        // independent copies
        root["copy"]!["y"] = 2;
        source["x"]!["y"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void Set_NullValue_WritesJsonNull()
    {
        var root = new JsonObject();
        P("$.a").Set(root, null);
        root.ToJsonString().ShouldBe("""{"a":null}""");
    }

    [Fact]
    public void TryRead_JsonElement_Navigates()
    {
        var root = JsonDocument.Parse("""{"a":{"b":[10,20],"x-y":7}}""").RootElement;
        P("$.a.b[1]").TryRead(root, out var v).ShouldBeTrue();
        v.GetInt32().ShouldBe(20);
        P("$.a.x-y").TryRead(root, out v).ShouldBeTrue();
        v.GetInt32().ShouldBe(7);
        P("$['a'].b").TryRead(root, out v).ShouldBeTrue();
        v.GetArrayLength().ShouldBe(2);
        P("$.a.zzz").TryRead(root, out _).ShouldBeFalse();
        P("$.a.b[5]").TryRead(root, out _).ShouldBeFalse();
        P("$").TryRead(root, out v).ShouldBeTrue();
        v.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void TryRead_JsonElement_Wildcard_ValueSurvivesLaterCalls()
    {
        using var doc = JsonDocument.Parse("""{"u":[{"id":"a"},{"id":"b"}]}""");
        P("$.u[*].id").TryRead(doc.RootElement, out var first).ShouldBeTrue();
        for (var i = 0; i < 50; i++)
            P("$.u[*].id").TryRead(doc.RootElement, out _).ShouldBeTrue();

        first.GetRawText().ShouldBe("[\"a\",\"b\"]");
    }

    [Fact]
    public void TryRead_JsonElement_Wildcard_ProjectsInOrder()
    {
        var root = JsonDocument.Parse("""{"u":[{"id":"a"},{"id":"b"},{"x":1},{"id":"c"}]}""").RootElement;
        P("$.u[*].id").TryRead(root, out var v).ShouldBeTrue();
        v.GetRawText().ShouldBe("""["a","b","c"]""");
    }

    [Fact]
    public void TryRead_JsonNode_Navigates()
    {
        var root = JsonNode.Parse("""{"a":{"b":[10,20]},"u":[{"id":1},{"id":2}]}""")!;
        P("$.a.b[0]").TryRead(root, out var v).ShouldBeTrue();
        v!.GetValue<int>().ShouldBe(10);
        P("$.u[*].id").TryRead(root, out v).ShouldBeTrue();
        v!.ToJsonString().ShouldBe("[1,2]");
        P("$.nope").TryRead(root, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryRead_JsonNode_NullValue_IsFoundWithNullNode()
    {
        var root = JsonNode.Parse("""{"a":null}""")!;
        P("$.a").TryRead(root, out var v).ShouldBeTrue();
        v.ShouldBeNull();
    }

    [Theory]
    [InlineData("name", """{"name":"alice"}""", new[] { "alice" })]
    [InlineData("customer.ownerUserId", """{"customer":{"ownerUserId":"alice"}}""", new[] { "alice" })]
    [InlineData("CUSTOMER.ownerUserId", """{"customer":{"OWNERUSERID":"alice"}}""", new[] { "alice" })]
    [InlineData("assignedUsers[*].userId", """{"assignedUsers":[{"userId":"alice"},{"userId":"bob"},{"x":1},{"userId":3},{"userId":true},{"userId":null}]}""", new[] { "alice", "bob", "3", "true" })]
    [InlineData("items[*].id", """{"items":[]}""", new string[0])]
    [InlineData("a[*]", """{"a":["x","y"]}""", new[] { "x", "y" })]
    [InlineData("a[*].b[*].c", """{"a":[{"b":[{"c":"1"},{"c":"2"}]},{"b":5},{"b":[{"c":"3"}]}]}""", new[] { "1", "2", "3" })]
    [InlineData("a[*]", """{"a":"notarray"}""", new string[0])]
    [InlineData("customer", """{"customer":{"id":1}}""", new string[0])]
    [InlineData("owner", """{"owner":null}""", new string[0])]
    [InlineData("amount", """{"amount":42}""", new[] { "42" })]
    [InlineData("a..b", """{"a":{"b":"x"}}""", new[] { "x" })]
    [InlineData(" a . b ", """{"a":{"b":"x"}}""", new[] { "x" })]
    [InlineData("a[0]", """{"a[0]":"literal","a":["z"]}""", new[] { "literal" })]
    [InlineData("a.b", """{"a":"str"}""", new string[0])]
    [InlineData("", """{"a":1}""", new string[0])]
    [InlineData("   ", """{"a":1}""", new string[0])]
    public void ContextPathResolver_SameResultsAsBefore(string path, string json, string[] expected)
    {
        var root = JsonDocument.Parse(json).RootElement;
        ContextPathResolver.Resolve(root, path).ShouldBe(expected);
        // the pre-split overload walks the same
        var segments = ContextPathResolver.SplitSegments(path);
        if (segments.Length > 0)
            ContextPathResolver.Resolve(root, segments, 0).ShouldBe(expected);
    }

    [Fact]
    public void ContextPathResolver_SplitSegments_ShapeUnchanged()
    {
        ContextPathResolver.SplitSegments("assignedUsers[*].userId").ShouldBe(["assignedUsers", "[*]", "userId"]);
        ContextPathResolver.SplitSegments(" a . b ").ShouldBe(["a", "b"]);
        ContextPathResolver.SplitSegments("").ShouldBeEmpty();
    }

    [Fact]
    public void ContextPathResolver_StartIndexEqualToLength_CollectsRoot()
    {
        var root = JsonDocument.Parse("\"leaf\"").RootElement;
        ContextPathResolver.Resolve(root, ["a"], 1).ShouldBe(["leaf"]);
    }
}
