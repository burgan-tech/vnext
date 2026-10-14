using System.Collections.Generic;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Publish-time <c>x-filterOperators</c> check: a declaration that the list query could never match (a query token, a
/// case variant, an unknown name) is refused with the name to declare.
/// </summary>
public sealed class FilterOperatorDefinitionTests
{
    private static IReadOnlyList<string> Validate(string field) =>
        FilterOperatorDefinition.Validate(JsonDocument.Parse(
            $$"""{ "type": "object", "properties": { "f": {{field}} } }""").RootElement);

    [Fact]
    public void EveryCanonicalName_IsAccepted()
        => Validate($$"""{ "type": "string", "x-filterOperators": {{JsonSerializer.Serialize(FilterOperatorDefinition.Canonical)}} }""")
            .ShouldBeEmpty();

    [Theory]
    [InlineData("ne", "neq")]
    [InlineData("ge", "gte")]
    [InlineData("le", "lte")]
    [InlineData("like", "contains")]
    [InlineData("match", "contains")]
    [InlineData("startswith", "startsWith")]
    [InlineData("endswith", "endsWith")]
    [InlineData("isnull", "isNull")]
    [InlineData("EQ", "eq")]
    [InlineData("StartsWith", "startsWith")]
    public void QueryTokensAndCaseVariants_AreRefused_NamingTheDeclaration(string written, string declared)
        => Validate($$"""{ "type": "string", "x-filterOperators": ["{{written}}"] }""").ShouldHaveSingleItem()
            .ShouldBe($"Field 'f': x-filterOperators value '{written}' is not a declaration name; declare '{declared}'.");

    [Fact]
    public void UnknownNames_DuplicatesAndBadShapes_AreRefused()
    {
        Validate("""{ "x-filterOperators": ["foo"] }""").ShouldHaveSingleItem().ShouldContain("'foo' is not an operator");
        Validate("""{ "x-filterOperators": ["eq", "eq"] }""").ShouldHaveSingleItem().ShouldContain("lists 'eq' more than once");
        Validate("""{ "x-filterOperators": "eq" }""").ShouldHaveSingleItem().ShouldContain("must be an array");
        Validate("""{ "x-filterOperators": [1] }""").ShouldHaveSingleItem().ShouldContain("must be an array");
    }

    [Fact]
    public void NestedAndItemPlacements_AreChecked_LiteralDataIsNot()
    {
        var errors = FilterOperatorDefinition.Validate(JsonDocument.Parse("""
            { "type": "object", "properties": {
                "customer": { "type": "object", "properties": { "age": { "type": "integer", "x-filterOperators": ["ge"] } } },
                "tags": { "type": "array", "items": { "type": "string", "x-filterOperators": ["like"] } },
                "note": { "type": "string", "examples": [ { "x-filterOperators": ["ge"] } ] } } }
            """).RootElement);

        errors.Count.ShouldBe(2);
        errors.ShouldContain(e => e.StartsWith("Field 'customer.age'"));
        errors.ShouldContain(e => e.StartsWith("Field 'tags'"));
    }

    [Fact]
    public void EmptyList_IsAccepted()
        => Validate("""{ "type": "string", "x-filterOperators": [] }""").ShouldBeEmpty();
}
