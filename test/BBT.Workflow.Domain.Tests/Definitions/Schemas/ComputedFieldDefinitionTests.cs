using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Publish-time <c>x-computed</c> rules: a runtime field is evaluated on every read of the master data and never stored,
/// so everything that would act on a stored value, or reference a value the reader cannot evaluate, is refused.
/// </summary>
public sealed class ComputedFieldDefinitionTests
{
    private static IReadOnlyList<string> Validate(string properties, string? type = "master", string extra = "") =>
        ComputedFieldDefinition.Validate(JsonDocument.Parse(
            $$"""{ "type": "object", {{extra}} "properties": { {{properties}} } }""").RootElement, type);

    private const string Names = """
        "firstName": { "type": "string" }, "lastName": { "type": "string" }, "kind": { "type": "string" },
        "a": { "type": "number" }, "b": { "type": "number" },
        """;

    [Fact]
    public void ValidRuntimeDeclaration_IsAccepted()
        => Validate(Names + """
            "fullName": { "type": "string", "readOnly": true, "x-computed": {
                "evaluatedBy": "runtime",
                "cases": [ { "when": { "allOf": [ { "field": "kind", "operator": "equals", "value": "person" },
                                                  { "not": { "field": "lastName", "operator": "isEmpty" } } ] },
                             "join": { "fields": ["firstName", "lastName"], "separator": " " } } ],
                "fallback": { "arithmetic": { "op": "add", "fields": ["a", "b"] } },
                "whenAbsent": "null" } }
            """).ShouldBeEmpty();

    [Fact]
    public void ClientDeclaration_IsNotInspected()
        => Validate("""
            "x": { "x-computed": { "evaluatedBy": "client", "cases": "anything the client understands" } }
            """, "transition").ShouldBeEmpty();

    [Fact]
    public void Server_IsRefusedAsRenamed()
        => Validate(""" "x": { "x-computed": { "evaluatedBy": "server" } } """)
            .ShouldHaveSingleItem().ShouldContain("renamed; use 'runtime'");

    [Theory]
    [InlineData("transition")]
    [InlineData("function")]
    [InlineData("component")]
    public void RuntimeDeclaration_IsRefused_OnANonRecordRole(string type)
        => Validate(Names + """ "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["a"] } } } } """, type)
            .ShouldHaveSingleItem().ShouldContain("use evaluatedBy 'client'");

    [Fact]
    public void RuntimeDeclaration_IsRefused_UnderItems()
        => Validate("""
                "rows": { "type": "array", "items": { "type": "object", "properties": {
                "a": { "type": "number" },
                "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["a"] } } } } } } }
                """)
            .ShouldHaveSingleItem().ShouldContain("reachable through nested 'properties'");

    [Theory]
    [InlineData(""" "x-indexed": true """, "x-indexed")]
    [InlineData(""" "x-filterOperators": ["eq"] """, "x-filterOperators")]
    [InlineData(""" "x-sortable": true """, "x-sortable")]
    [InlineData(""" "x-roles": [ { "role": "a", "grant": "allow" } ] """, "x-roles")]
    [InlineData(""" "x-masking": { "operator": "mask" } """, "x-masking")]
    [InlineData(""" "x-encryption": { "type": "encrypt" } """, "x-encryption")]
    [InlineData(""" "x-storage": { "binding": "files" } """, "x-storage")]
    public void StoredValueCompanions_AreRefused(string companion, string keyword)
        => Validate(Names + $$"""
                "t": { "type": "string", {{companion}},
                "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["a"] } } } }
                """)
            .ShouldHaveSingleItem().ShouldContain(keyword);

    [Fact]
    public void Required_IsRefused()
        => Validate(Names + """ "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["a"] } } } } """,
                extra: """ "required": ["t"], """)
            .ShouldHaveSingleItem().ShouldContain("cannot be 'required'");

    [Fact]
    public void ShapeErrors_AreReported()
    {
        Validate(""" "t": { "x-computed": { "evaluatedBy": "runtime" } } """)
            .ShouldHaveSingleItem().ShouldContain("needs 'cases' or a 'fallback'");
        Validate(Names + """ "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["a"] }, "arithmetic": { "op": "add", "fields": ["a", "b"] } } } } """)
            .ShouldContain(e => e.Contains("exactly one of 'join' or 'arithmetic'"));
        Validate(Names + """ "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "arithmetic": { "op": "pow", "fields": ["a"] } } } } """)
            .Count.ShouldBe(2);
        Validate(Names + """ "t": { "x-computed": { "evaluatedBy": "runtime", "cases": [ { "when": { "field": "a", "operator": "gt" }, "join": { "fields": ["a"] } } ] } } """)
            .ShouldHaveSingleItem().ShouldContain("operator must be one of");
        Validate(Names + """ "t": { "x-computed": { "evaluatedBy": "runtime", "whenAbsent": "zero", "fallback": { "join": { "fields": ["a"] } } } } """)
            .ShouldHaveSingleItem().ShouldContain("whenAbsent");
    }

    [Fact]
    public void References_MustBeSiblings_NotComputed_NotEncrypted()
    {
        Validate(Names + """ "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["missing"] } } } } """)
            .ShouldHaveSingleItem().ShouldContain("'missing', which is not a property");
        Validate(Names + """
            "u": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["a"] } } } },
            "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["u"] } } } }
            """).ShouldHaveSingleItem().ShouldContain("do not chain");
        Validate("""
            "tckn": { "type": "string", "x-encryption": { "type": "encrypt" } },
            "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["tckn"] } } } }
            """).ShouldHaveSingleItem().ShouldContain("encrypted at rest");
        Validate("""
            "customer": { "type": "object", "properties": { "name": { "type": "string" } } },
            "t": { "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["customer.name"] } } } }
            """).ShouldBeEmpty();
    }
}
