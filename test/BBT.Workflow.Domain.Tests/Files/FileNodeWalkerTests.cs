using System.Linq;
using System.Text.Json.Nodes;
using BBT.Workflow.Definitions.Schemas;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Files;

public sealed class FileNodeWalkerTests
{
    [Fact]
    public void Find_ExpandsArraysAndSkipsNonObjects()
    {
        var root = JsonNode.Parse("""{ "files": [ { "content": "AA==" }, 5, { "file": "x" } ], "p": { "content": "AQ==" } }""");

        FileNodeWalker.Find(root, new FileStorageField(["files", "[]"], "b")).Select(n => n.Path)
            .ShouldBe(["files[0]", "files[2]"]);
        FileNodeWalker.Find(root, new FileStorageField(["p"], "b")).Single().Path.ShouldBe("p");
        FileNodeWalker.Find(root, new FileStorageField(["missing"], "b")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{ "files": [ 5, { "file": "x" }, { "content": "AA==" } ] }""", true)]
    [InlineData("""{ "files": [ { "file": "x" } ], "p": { "file": "y" } }""", false)]
    [InlineData("""{ "p": { "content": 5 } }""", true)]
    [InlineData("""{ "p": "content", "other": { "content": "AA==" } }""", false)]
    [InlineData("""{ "files": { "content": "AA==" } }""", false)]
    public void AnyContent_ProbesTheFieldPathsReadOnly(string json, bool expected)
    {
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
        FileNodeWalker.AnyContent(root, [new FileStorageField(["files", "[]"], "b"), new FileStorageField(["p"], "b")])
            .ShouldBe(expected);
    }

    [Fact]
    public void Handle_RoundTripsWithExactPropertyNames()
    {
        var h = new FileHandle("c", "f", "a.pdf", "application/pdf", 3, "ab", new FileOwner("d", "fl", "i"));
        var json = h.ToJsonNode().ToJsonString();
        json.ShouldBe("""{"component":"c","file":"f","name":"a.pdf","mimeType":"application/pdf","size":3,"eTag":"ab","owner":{"domain":"d","flow":"fl","instance":"i"}}""");
        FileHandle.TryRead(JsonNode.Parse(json)!.AsObject()).ShouldBe(h);
    }

    [Theory]
    [InlineData("""{"component":"c","file":"f","size":3,"eTag":"ab"}""")]
    [InlineData("""{"component":"c","file":"f","size":3,"eTag":"ab","owner":null}""")]
    [InlineData("""{"component":"c","file":"f","size":3,"owner":{"domain":"d","flow":"fl","instance":"i"}}""")]
    [InlineData("""{"component":"c","file":"f","size":"3","eTag":"ab","owner":{"domain":"d","flow":"fl","instance":"i"}}""")]
    [InlineData("""{"component":5,"file":"f","size":3,"eTag":"ab","owner":{"domain":"d","flow":"fl","instance":"i"}}""")]
    [InlineData("""{"component":"c","file":"f","size":3,"eTag":"ab","owner":{"domain":"d","flow":"fl"}}""")]
    public void TryRead_RejectsIncompleteHandles(string json)
        => FileHandle.TryRead(JsonNode.Parse(json)!.AsObject()).ShouldBeNull();

    [Fact]
    public void HasContent_TrueOnlyForStringContent()
    {
        FileNodeWalker.HasContent(JsonNode.Parse("""{"content":"AA=="}""")!.AsObject()).ShouldBeTrue();
        FileNodeWalker.HasContent(JsonNode.Parse("""{"content":5}""")!.AsObject()).ShouldBeFalse();
        FileNodeWalker.HasContent(JsonNode.Parse("""{"file":"x"}""")!.AsObject()).ShouldBeFalse();
    }
}
