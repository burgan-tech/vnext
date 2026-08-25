using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Unit tests for <see cref="SchemaAnnotationWalker"/>, the single traversal every vocabulary
/// parser now shares. These pin the path grammar, because three parsers previously disagreed
/// about it and a path mismatch silently disables an annotation.
/// </summary>
public sealed class SchemaAnnotationWalkerTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string[] Paths(string json)
        => SchemaAnnotationWalker.Walk(Json(json)).Select(node => node.Path).ToArray();

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("""{ "type": "object" }""")]
    public void Walk_WhenNothingToTraverse_ReturnsEmpty(string json)
        => Paths(json).ShouldBeEmpty();

    [Fact]
    public void Walk_DoesNotYieldTheRoot()
        => Paths("""{ "properties": { "a": { "type": "string" } } }""").ShouldBe(["a"]);

    [Fact]
    public void Walk_YieldsNestedPropertiesDotted()
    {
        var paths = Paths("""
        {
          "properties": {
            "amount": { "type": "number" },
            "customer": {
              "type": "object",
              "properties": {
                "email": { "type": "string" },
                "address": { "type": "object", "properties": { "city": { "type": "string" } } }
              }
            }
          }
        }
        """);

        paths.ShouldBe(["amount", "customer", "customer.email", "customer.address", "customer.address.city"]);
    }

    [Fact]
    public void Walk_MarksArrayItemsWithBrackets()
    {
        var paths = Paths("""
        {
          "properties": {
            "cards": {
              "type": "array",
              "items": { "type": "object", "properties": { "number": { "type": "string" } } }
            }
          }
        }
        """);

        paths.ShouldBe(["cards", "cards[]", "cards[].number"]);
    }

    [Fact]
    public void Walk_HandlesNestedArrays()
    {
        var paths = Paths("""
        {
          "properties": {
            "matrix": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } }
          }
        }
        """);

        paths.ShouldBe(["matrix", "matrix[]", "matrix[][]"]);
    }

    [Fact]
    public void Walk_SkipsNonObjectPropertySchemas()
    {
        // "properties": { "x": true } is legal JSON Schema and used to throw here.
        Paths("""{ "properties": { "x": true, "y": { "type": "string" } } }""").ShouldBe(["y"]);
    }

    [Fact]
    public void Walk_DoesNotTraverseTupleFormItems()
    {
        Paths("""
        {
          "properties": {
            "pair": { "type": "array", "items": [ { "type": "string" }, { "type": "number" } ] }
          }
        }
        """).ShouldBe(["pair"]);
    }

    [Fact]
    public void Walk_DoesNotDescendIntoRefsOrComposition()
    {
        var paths = Paths("""
        {
          "$defs": { "address": { "properties": { "city": { "type": "string" } } } },
          "properties": {
            "shipping": { "$ref": "#/$defs/address" },
            "either": { "oneOf": [ { "properties": { "a": { "type": "string" } } } ] }
          }
        }
        """);

        // Deliberate: the runtime resolves neither, so the walk must not pretend it does.
        paths.ShouldBe(["shipping", "either"]);
    }

    [Fact]
    public void Walk_TreatsAPropertyLiterallyNamedPropertiesAsData()
    {
        // A business property called "properties" must not be mistaken for the keyword.
        Paths("""{ "properties": { "properties": { "type": "string" } } }""").ShouldBe(["properties"]);
    }

    [Fact]
    public void FindUnreachable_ReportsAnnotationsUnderUnfollowedKeywords()
    {
        var unreachable = SchemaAnnotationWalker.FindUnreachable(
            Json("""
            {
              "$defs": { "a": { "properties": { "ssn": { "x-sensitive": { "enabled": true } } } } },
              "properties": {
                "ok": { "x-sensitive": { "enabled": true } },
                "either": { "oneOf": [ { "properties": { "pan": { "x-sensitive": { "enabled": true } } } } ] },
                "extra": { "additionalProperties": { "x-sensitive": { "enabled": true } } }
              }
            }
            """),
            "x-sensitive");

        unreachable.ShouldContain("$defs.a.properties.ssn");
        unreachable.ShouldContain("properties.either.oneOf[0].properties.pan");
        unreachable.ShouldContain("properties.extra.additionalProperties");
        unreachable.ShouldNotContain("properties.ok");
    }

    [Fact]
    public void FindUnreachable_IsSilentWhenEverythingIsReachable()
    {
        SchemaAnnotationWalker.FindUnreachable(
                Json("""
                {
                  "properties": {
                    "email": { "x-sensitive": { "enabled": true } },
                    "cards": { "items": { "properties": { "pan": { "x-sensitive": { "enabled": true } } } } }
                  }
                }
                """),
                "x-sensitive")
            .ShouldBeEmpty();
    }

    [Fact]
    public void FindUnreachable_ReportsTupleFormItems()
    {
        SchemaAnnotationWalker.FindUnreachable(
                Json("""
                { "properties": { "pair": { "items": [ { "x-sensitive": { "enabled": true } } ] } } }
                """),
                "x-sensitive")
            .ShouldContain("properties.pair.items[0]");
    }

    [Fact]
    public void SharedWalk_MakesRolesAndFilterMetadataAgreeOnArrayPaths()
    {
        // The regression this consolidation exists to prevent: SchemaRolesParser used to skip
        // "items" entirely, so an x-roles inside an array was silently inert while
        // SchemaFilterMetadataResolver had its own idea of the same tree.
        var schema = Json("""
        {
          "properties": {
            "cards": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "number": {
                    "type": "string",
                    "x-roles": [ { "role": "ops", "grant": "allow" } ],
                    "x-sortable": true
                  }
                }
              }
            }
          }
        }
        """);

        SchemaRolesParser.ParsePropertyRoles(schema).Keys.ShouldContain("cards[].number");

        var metadata = SchemaFilterMetadataResolver.Resolve(schema)!.GetFieldMetadata("cards[].number");
        metadata.ShouldNotBeNull();
        metadata.Sortable.ShouldBeTrue();
    }
}
