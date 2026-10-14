using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>Runtime <c>x-computed</c> evaluation over a data document.</summary>
public sealed class ComputedFieldEvaluatorTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Apply(string schemaProperties, string data)
    {
        var rules = ComputedFieldParser.Parse(Json($$"""{ "type": "object", "properties": { {{schemaProperties}} } }"""));
        return ComputedFieldEvaluator.Apply(Json(data), rules);
    }

    private const string FullName = """
        "fullName": { "x-computed": { "evaluatedBy": "runtime",
            "cases": [ { "when": { "field": "kind", "operator": "equals", "value": "corporate" }, "join": { "fields": ["title"] } } ],
            "fallback": { "join": { "fields": ["firstName", "lastName"] } } } }
        """;

    [Fact]
    public void FirstMatchingCase_Wins_ElseFallback()
    {
        Apply(FullName, """{ "kind": "corporate", "title": "Acme A.Ş.", "firstName": "A" }""")
            .GetProperty("fullName").GetString().ShouldBe("Acme A.Ş.");
        Apply(FullName, """{ "kind": "person", "firstName": "Ada", "lastName": "Lovelace" }""")
            .GetProperty("fullName").GetString().ShouldBe("Ada Lovelace");
    }

    [Fact]
    public void Join_SkipsAbsentAndEmptyValues_AndUsesTheSeparator()
        => Apply("""
            "label": { "x-computed": { "evaluatedBy": "runtime",
                "fallback": { "join": { "fields": ["code", "missing", "empty", "count", "active"], "separator": " · " } } } }
            """, """{ "code": "TR", "empty": "", "count": 3, "active": true }""")
            .GetProperty("label").GetString().ShouldBe("TR · 3 · true");

    [Theory]
    [InlineData("add", "12.5")]
    [InlineData("subtract", "7.5")]
    [InlineData("multiply", "25.00")]
    [InlineData("divide", "4")]
    public void Arithmetic_IsLeftToRight_InDecimal(string op, string expected)
        => Apply($$""" "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "arithmetic": { "op": "{{op}}", "fields": ["a", "b"] } } } } """,
                """{ "a": 10.0, "b": 2.5 }""")
            .GetProperty("t").GetRawText().ShouldBe(expected);

    [Theory]
    [InlineData("""{ "a": 1 }""")]
    [InlineData("""{ "a": 1, "b": "2" }""")]
    [InlineData("""{ "a": 1, "b": 0 }""")]
    public void UnresolvableArithmetic_FollowsWhenAbsent(string data)
    {
        const string Divide = """ "fallback": { "arithmetic": { "op": "divide", "fields": ["a", "b"] } } """;
        Apply($$""" "t": { "x-computed": { "evaluatedBy": "runtime", {{Divide}} } } """, data)
            .GetProperty("t").GetString().ShouldBe(string.Empty);
        Apply($$""" "t": { "x-computed": { "evaluatedBy": "runtime", "whenAbsent": "null", {{Divide}} } } """, data)
            .GetProperty("t").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Conditions_CoverOperatorsAndCombinators()
    {
        const string TierSchema = """
            "tier": { "x-computed": { "evaluatedBy": "runtime", "cases": [
                { "when": { "allOf": [ { "field": "amount", "operator": "greaterThanOrEquals", "value": 1000 },
                                       { "field": "currency", "operator": "in", "value": ["TRY", "USD"] } ] },
                  "join": { "fields": ["gold"] } },
                { "when": { "anyOf": [ { "field": "code", "operator": "startsWith", "value": "VIP" },
                                       { "field": "tags", "operator": "contains", "value": "vip" } ] },
                  "join": { "fields": ["vip"] } },
                { "when": { "not": { "field": "note", "operator": "isEmpty" } }, "join": { "fields": ["note"] } } ] } }
            """;
        string? Tier(string data) => Apply(TierSchema, data).GetProperty("tier").GetString();

        Tier("""{ "amount": 1000, "currency": "TRY", "gold": "G" }""").ShouldBe("G");
        Tier("""{ "amount": 1000, "currency": "EUR", "code": "VIP-1", "vip": "V" }""").ShouldBe("V");
        Tier("""{ "amount": 5, "tags": ["x", "vip"], "vip": "V" }""").ShouldBe("V");
        Tier("""{ "amount": 5, "note": "n" }""").ShouldBe("n");
        Tier("""{ "amount": 5 }""").ShouldBe(string.Empty);
    }

    [Fact]
    public void NestedField_IsWrittenIntoItsParent_AndSkippedWhenTheParentIsAbsent()
    {
        const string Nested = """
            "customer": { "type": "object", "properties": {
                "name": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["first", "address.city"], "separator": ", " } } } } } }
            """;

        Apply(Nested, """{ "customer": { "first": "Ada", "address": { "city": "London" } } }""")
            .GetProperty("customer").GetProperty("name").GetString().ShouldBe("Ada, London");

        var withoutCustomer = Json("""{ "other": 1 }""");
        var rules = ComputedFieldParser.Parse(Json($$"""{ "properties": { {{Nested}} } }"""));
        ComputedFieldEvaluator.Apply(withoutCustomer, rules).GetRawText().ShouldBe(withoutCustomer.GetRawText());
    }

    [Fact]
    public void ClientDeclarations_AreIgnored_AndAStoredValueIsOverwritten()
    {
        Apply(""" "c": { "x-computed": { "evaluatedBy": "client", "fallback": { "join": { "fields": ["a"] } } } } """,
                """{ "a": "x" }""")
            .TryGetProperty("c", out _).ShouldBeFalse();

        Apply(""" "c": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["a"] } } } } """,
                """{ "a": "fresh", "c": "stale" }""")
            .GetProperty("c").GetString().ShouldBe("fresh");
    }
}
