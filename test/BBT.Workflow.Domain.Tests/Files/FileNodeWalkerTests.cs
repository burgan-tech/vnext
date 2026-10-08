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
    [InlineData("""{"component":"c","file":"f","size":3,"eTag":"","owner":{"domain":"d","flow":"fl","instance":"i"}}""")]
    [InlineData("""{"component":"c","file":"f","size":3,"eTag":"  ","owner":{"domain":"d","flow":"fl","instance":"i"}}""")]
    [InlineData("""{"component":"c","file":"f","size":3,"eTag":"ab","owner":{"domain":" ","flow":"fl","instance":"i"}}""")]
    [InlineData("""{"component":"c","file":"f","size":3,"eTag":"ab","owner":{"domain":"d","flow":"","instance":"i"}}""")]
    [InlineData("""{"component":"c","file":"f","size":3,"eTag":"ab","owner":{"domain":"d","flow":"fl","instance":""}}""")]
    public void TryRead_RejectsIncompleteHandles(string json)
        => FileHandle.TryRead(JsonNode.Parse(json)!.AsObject()).ShouldBeNull();

    [Fact]
    public void HasContent_TrueOnlyForStringContent()
    {
        FileNodeWalker.HasContent(JsonNode.Parse("""{"content":"AA=="}""")!.AsObject()).ShouldBeTrue();
        FileNodeWalker.HasContent(JsonNode.Parse("""{"content":5}""")!.AsObject()).ShouldBeFalse();
        FileNodeWalker.HasContent(JsonNode.Parse("""{"file":"x"}""")!.AsObject()).ShouldBeFalse();
    }

    [Theory]
    [InlineData("0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80", "b", true)]
    [InlineData("0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80", "other", false)]
    [InlineData("0b9f6f3e1c1a4a7e9c553f1d2a6b7c80", "b", false)]
    [InlineData("{0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80}", "b", false)]
    [InlineData("../x", "b", false)]
    public void IsAllowed_RequiresAGuidFileIdAndAnAllowedComponent(string file, string component, bool expected)
    {
        var h = new FileHandle(component, file, null, null, 1, "e", new FileOwner("d", "f", "i"));
        h.IsAllowed(new System.Collections.Generic.HashSet<string> { "b" }).ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{ "a": 1, "b": [ { "c": "x" } ] }""", false)]
    [InlineData("""{ "a": { "b": [ { "content": 5 } ] } }""", true)]
    [InlineData("""{ "deep": [ [ { "file": "x" } ] ] }""", true)]
    [InlineData("""{ "p": "content" }""", false)]
    [InlineData("""{ "p": "\"file\"" }""", false)]                // the literal as a value: the exact walk decides
    [InlineData("""{ "p": { "\u0066ile": "x" } }""", true)]        // an escaped name never slips past the fast path
    [InlineData("""{ "p": { "c\u006fntent": "AA==" } }""", true)]
    public void AnyFileShapedNode_LooksAtEveryObject(string json, bool expected)
        => FileNodeWalker.AnyFileShapedNode(System.Text.Json.JsonDocument.Parse(json).RootElement).ShouldBe(expected);

    [Theory]
    [InlineData("""{ "files": [ { "file": "x" } ] }""", true)]
    [InlineData("""{ "p": { "content": "AA==" } }""", true)]
    [InlineData("""{ "p": { "name": "a" }, "other": { "file": "x" } }""", false)]
    public void AnyContentOrFile_ProbesTheFieldPaths(string json, bool expected)
    {
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
        FileNodeWalker.AnyContentOrFile(root, [new FileStorageField(["files", "[]"], "b"), new FileStorageField(["p"], "b")])
            .ShouldBe(expected);
    }
}
