using System.Linq;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

public sealed class FileStorageSchemaParserTests
{
    private static JsonElement Schema(string properties) =>
        JsonDocument.Parse($$"""{ "type": "object", "properties": { {{properties}} } }""").RootElement;

    [Fact]
    public void Parse_FindsObjectArrayAndNestedFields()
    {
        var fields = FileStorageSchemaParser.Parse(Schema("""
            "passport": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } },
            "files": { "type": "array", "items": { "type": "object", "x-storage": { "binding": "vnext-blob-s3" } } },
            "customer": { "type": "object", "properties": {
                "tax": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } } } }
            """));

        fields.Select(f => (f.Path, f.Binding)).ShouldBe(new[]
        {
            ("passport", "vnext-blob-local"),
            ("files[]", "vnext-blob-s3"),
            ("customer.tax", "vnext-blob-local"),
        }, ignoreOrder: true);
    }

    [Fact]
    public void Parse_NoKeyword_ReturnsEmpty()
        => FileStorageSchemaParser.Parse(Schema("""  "a": { "type": "string" } """)).ShouldBeEmpty();

    [Theory]
    [InlineData(""" "f": { "type": "object", "x-storage": "file" } """, "must be an object")]
    [InlineData(""" "f": { "type": "object", "x-storage": { } } """, "binding")]
    [InlineData(""" "f": { "type": "object", "x-storage": { "binding": "" } } """, "binding")]
    [InlineData(""" "f": { "type": "object", "x-storage": { "binding": "b", "x": 1 } } """, "unknown")]
    [InlineData(""" "f": { "type": "string", "x-storage": { "binding": "b" } } """, "type")]
    [InlineData(""" "f": { "type": "object", "properties": { "content": { "type": "string" } }, "x-storage": { "binding": "b" } } """, "content")]
    [InlineData(""" "f": { "type": "object", "required": ["content"], "x-storage": { "binding": "b" } } """, "content")]
    [InlineData(""" "f": { "type": "array", "items": { "type": "array", "items": { "type": "object", "x-storage": { "binding": "b" } } } } """, "nested array")]
    public void Validate_RejectsBadDeclarations(string properties, string fragment)
    {
        var errors = FileStorageSchemaParser.Validate(Schema(properties));
        errors.Count.ShouldBe(1, string.Join(" | ", errors));
        errors[0].ShouldContain(fragment);
    }

    [Fact]
    public void Validate_RejectsKeywordUnderDefs()
    {
        var root = JsonDocument.Parse("""
            { "type": "object", "$defs": { "doc": { "type": "object", "x-storage": { "binding": "b" } } } }
            """).RootElement;
        FileStorageSchemaParser.Validate(root).Single().ShouldContain("reachable");
    }

    [Fact]
    public void Validate_AcceptsValidShapes()
        => FileStorageSchemaParser.Validate(Schema("""
            "passport": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } },
            "files": { "type": "array", "items": { "type": "object", "x-storage": { "binding": "b" } } }
            """)).ShouldBeEmpty();

    [Theory]
    [InlineData(""" "f": { "type": [1], "x-storage": { "binding": "b" } } """, "type")]
    [InlineData(""" "f": { "type": "object", "required": [1], "x-storage": { "binding": "b" } } """, null)]
    public void Validate_MalformedTypeOrRequired_DoesNotThrow(string properties, string? fragment)
    {
        var errors = FileStorageSchemaParser.Validate(Schema(properties));
        if (fragment is null) errors.ShouldBeEmpty();
        else errors.Single().ShouldContain(fragment);
    }

    [Fact]
    public void Validate_RejectsRootLevelKeyword()
    {
        var root = JsonDocument.Parse("""{ "type": "object", "x-storage": { "binding": "b" } }""").RootElement;
        FileStorageSchemaParser.Validate(root).Single().ShouldContain("root");
    }

    [Fact]
    public void Validate_RejectsKeywordNestedInsideFileNode()
        => FileStorageSchemaParser.Validate(Schema("""
            "f": { "type": "object", "x-storage": { "binding": "b" },
                   "properties": { "g": { "type": "object", "x-storage": { "binding": "b" } } } }
            """)).Single().ShouldContain("inside another");

    [Theory]
    [InlineData(""" "f": { "type": "object", "allOf": [ { "x-storage": { "binding": "b" } } ] } """)]
    [InlineData(""" "f": { "type": "object", "oneOf": [ { "x-storage": { "binding": "b" } } ] } """)]
    [InlineData(""" "f": { "type": "object", "if": { "x-storage": { "binding": "b" } } } """)]
    [InlineData(""" "f": { "type": "object", "then": { "x-storage": { "binding": "b" } } } """)]
    public void Validate_RejectsKeywordUnderCombinatorsAndConditionals(string properties)
        => FileStorageSchemaParser.Validate(Schema(properties)).Single().ShouldContain("reachable");
}
