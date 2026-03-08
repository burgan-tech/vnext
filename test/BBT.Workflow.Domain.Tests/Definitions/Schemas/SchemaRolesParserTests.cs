using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Unit tests for SchemaRolesParser (master schema field-level "roles" vocabulary).
/// </summary>
public sealed class SchemaRolesParserTests
{
    [Fact]
    public void ParsePropertyRoles_WhenEmptyObject_ReturnsEmpty()
    {
        var schema = JsonDocument.Parse("{}").RootElement;
        var result = SchemaRolesParser.ParsePropertyRoles(schema);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ParsePropertyRoles_WhenPropertiesWithoutRoles_ReturnsEmpty()
    {
        var schema = JsonDocument.Parse(@"{
            ""properties"": {
                ""amount"": { ""type"": ""number"" },
                ""publicStatus"": { ""type"": ""string"" }
            }
        }").RootElement;
        var result = SchemaRolesParser.ParsePropertyRoles(schema);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ParsePropertyRoles_WhenPropertiesWithRoles_ReturnsPathToGrants()
    {
        var schema = JsonDocument.Parse(@"{
            ""properties"": {
                ""amount"": {
                    ""type"": ""number"",
                    ""roles"": [
                        { ""role"": ""morph-idm.maker"", ""grant"": ""allow"" },
                        { ""role"": ""morph-idm.approver"", ""grant"": ""allow"" }
                    ]
                },
                ""internalNotes"": {
                    ""type"": ""string"",
                    ""roles"": [
                        { ""role"": ""morph-idm.approver"", ""grant"": ""allow"" }
                    ]
                },
                ""publicStatus"": { ""type"": ""string"" }
            }
        }").RootElement;
        var result = SchemaRolesParser.ParsePropertyRoles(schema);
        result.Count.ShouldBe(2);
        result.ShouldContainKey("amount");
        result.ShouldContainKey("internalNotes");
        result["amount"].Count.ShouldBe(2);
        result["amount"][0].Role.ShouldBe("morph-idm.maker");
        result["amount"][0].Grant.ShouldBe(GrantKind.Allow);
        result["internalNotes"].Count.ShouldBe(1);
        result["internalNotes"][0].Role.ShouldBe("morph-idm.approver");
    }

    [Fact]
    public void ParsePropertyRoles_WhenNestedProperties_ParsesDotPath()
    {
        var schema = JsonDocument.Parse(@"{
            ""properties"": {
                ""nested"": {
                    ""type"": ""object"",
                    ""properties"": {
                        ""foo"": {
                            ""type"": ""string"",
                            ""roles"": [{ ""role"": ""admin"", ""grant"": ""allow"" }]
                        }
                    }
                }
            }
        }").RootElement;
        var result = SchemaRolesParser.ParsePropertyRoles(schema);
        result.Count.ShouldBe(1);
        result.ShouldContainKey("nested.foo");
        result["nested.foo"][0].Role.ShouldBe("admin");
    }

    [Fact]
    public void ParsePropertyRoles_WhenInvalidGrant_SkipsEntry()
    {
        var schema = JsonDocument.Parse(@"{
            ""properties"": {
                ""a"": {
                    ""roles"": [
                        { ""role"": ""r1"", ""grant"": ""allow"" },
                        { ""role"": ""r2"", ""grant"": ""invalid"" }
                    ]
                }
            }
        }").RootElement;
        var result = SchemaRolesParser.ParsePropertyRoles(schema);
        result.Count.ShouldBe(1);
        result["a"].Count.ShouldBe(1);
        result["a"][0].Role.ShouldBe("r1");
    }
}
