using System.Linq;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Publish-time rules for <c>x-masking</c>. Each rejected shape is one the read path would otherwise
/// have to guess about — silently ignore, or honour in a way that leaks.
/// </summary>
public sealed class FieldMaskingDefinitionTests
{
    private static JsonElement Schema(string properties) =>
        JsonDocument.Parse($$"""{ "type": "object", "properties": { {{properties}} } }""").RootElement;

    private static string Only(string properties, string? type = "master", string keyword = "x-masking")
    {
        var errors = FieldMaskingDefinition.Validate(Schema(properties), type);
        errors.Count.ShouldBe(1, string.Join(" | ", errors.Select(e => e.Message)));
        errors[0].Keyword.ShouldBe(keyword);
        return errors[0].Message;
    }

    [Fact]
    public void ValidMaskAndReplaceDeclarations_PassOnAMasterSchema()
    {
        var errors = FieldMaskingDefinition.Validate(Schema("""
            "iban": { "type": "string", "x-masking": { "operator": "mask",
                      "params": { "keepFirst": 4, "keepLast": 2, "maskingChar": "#" },
                      "roles": [ { "role": "morph-idm.auditor", "grant": "allow" },
                                 { "role": "$InstanceStarter", "grant": "allow" },
                                 { "role": "$user.$.context.Instance.Data.ownerId", "grant": "allow" } ] } },
            "note": { "type": ["string", "null"], "x-masking": { "operator": "replace", "params": { "value": "[gizli]" } } },
            "customer": { "type": "object", "properties": {
                "tckn": { "type": "string", "x-encryption": { "type": "none" }, "x-masking": { "operator": "mask" } } } }
            """), "master");

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void SchemaWithoutTransforms_IsNeverAffected()
        => FieldMaskingDefinition.Validate(Schema("""
            "b": { "type": "string", "x-filterOperators": ["eq"] },
            "c": { "type": "integer", "x-encryption": { "type": "none" } }
            """), "workflow").ShouldBeEmpty();

    /// <summary>
    /// <c>persisted</c> and <c>transport</c> were declared by the vocabulary but never enforced; they are removed in
    /// favour of <c>encrypt</c> (vnext-meta deprecations.json). A schema still carrying them is refused at publish
    /// with a message that names the replacement, rather than silently doing nothing.
    /// </summary>
    [Theory]
    [InlineData("persisted")]
    [InlineData("transport")]
    public void RemovedEncryptionTypes_AreRejected_NamingTheReplacement(string type)
        => Only($$"""  "a": { "type": "string", "x-encryption": { "type": "{{type}}" } }  """, keyword: "x-encryption")
            .ShouldContain("has been removed; use 'encrypt'");

    [Theory]
    [InlineData("""{ "type": "whatever" }""", "not supported. Allowed: none, hash, encrypt")]
    [InlineData("""{ "type": "Hash" }""", "lower case ('hash')")]
    [InlineData("""{ "type": "ENCRYPT" }""", "lower case ('encrypt')")]
    [InlineData("""{ }""", "type is required")]
    [InlineData("""{ "type": "none", "extra": 1 }""", "does not support 'extra'")]
    [InlineData("\"persisted\"", "must be an object")]
    public void MalformedEncryptionDeclarations_AreRejected(string declaration, string expected)
        => Only($$"""  "a": { "type": "string", "x-encryption": {{declaration}} }  """, keyword: "x-encryption")
            .ShouldContain(expected);

    [Fact]
    public void Encrypt_OnAStringProperty_IsAccepted_WithAllowOnlyRolesAndMetadata()
        => FieldMaskingDefinition.Validate(Schema("""
            "customer": { "type": "object", "properties": {
              "email": { "type": ["string", "null"], "x-encryption": { "type": "encrypt",
                "roles": [ { "role": "auditor", "grant": "allow" } ], "purpose": "kvkk", "redactInLogs": true, "retentionDays": 30 } } } }
            """), "workflow").ShouldBeEmpty();

    [Theory]
    [InlineData("""  "a": { "type": "integer", "x-encryption": { "type": "encrypt" } }  """, "type \"string\"")]
    [InlineData("""  "a": { "type": "string", "x-encryption": { "type": "encrypt", "params": { "key": "x" } } }  """, "params is not supported for type 'encrypt'")]
    [InlineData("""  "a": { "type": "string", "x-indexed": true, "x-encryption": { "type": "encrypt" } }  """, "x-indexed")]
    [InlineData("""  "a": { "type": "string", "x-sortable": true, "x-encryption": { "type": "encrypt" } }  """, "x-sortable")]
    [InlineData("""  "a": { "type": "string", "x-filterOperators": ["eq"], "x-encryption": { "type": "encrypt" } }  """, "x-filterOperators")]
    public void EncryptOutsideItsSupportedShape_IsRejected(string properties, string expected)
        => Only(properties, keyword: "x-encryption").ShouldContain(expected);

    [Fact]
    public void EncryptUnderItems_IsRejected()
        => Only("""  "list": { "type": "array", "items": { "type": "object", "properties": { "e": { "type": "string", "x-encryption": { "type": "encrypt" } } } } }  """, keyword: "x-encryption")
            .ShouldContain("reachable through nested 'properties'");

    [Fact]
    public void HashWithXIndexed_IsRejected()
        => Only("""  "a": { "type": "string", "x-indexed": true, "x-encryption": { "type": "hash" } }  """, keyword: "x-encryption")
            .ShouldContain("x-indexed");

    /// <summary>
    /// Domains publish their data schemas as <c>workflow</c>, <c>schema</c> or <c>master</c> (vnext-example has 22
    /// <c>workflow</c> and 10 <c>schema</c>, no <c>master</c>). Gating on one spelling rejected every real one.
    /// </summary>
    [Theory]
    [InlineData("master")]
    [InlineData("workflow")]
    [InlineData("schema")]
    [InlineData(null)]
    public void MaskingAndHash_AreAcceptedOnEverySchemaComponentType(string? type)
        => FieldMaskingDefinition.Validate(Schema("""
            "a": { "type": "string", "x-masking": { "operator": "mask" } },
            "b": { "type": "string", "x-encryption": { "type": "hash" } }
            """), type).ShouldBeEmpty();

    [Theory]
    [InlineData("""  "a": { "type": "number", "x-masking": { "operator": "mask" } }  """)]
    [InlineData("""  "a": { "x-masking": { "operator": "mask" } }  """)]
    [InlineData("""  "a": { "type": ["string", "integer"], "x-masking": { "operator": "mask" } }  """)]
    public void NonStringProperty_IsRejected(string property)
        => Only(property).ShouldContain("requires the property to declare type \"string\"");

    [Theory]
    [InlineData("redact")]
    [InlineData("hash")]
    [InlineData("encrypt")]
    public void OperatorOutsidePhaseOne_IsRejected(string op)
        => Only($$"""  "a": { "type": "string", "x-masking": { "operator": "{{op}}" } }  """)
            .ShouldContain("must be 'mask' or 'replace'");

    [Fact]
    public void DenyGrantInExemptionList_IsRejected()
        => Only("""  "a": { "type": "string", "x-masking": { "operator": "mask", "roles": [ { "role": "teller", "grant": "deny" } ] } }  """)
            .ShouldContain("only grant 'allow' is accepted");

    [Fact]
    public void MalformedDynamicExemptionRole_IsRejected()
        => Only("""  "a": { "type": "string", "x-masking": { "operator": "mask", "roles": [ { "role": "$user.customer", "grant": "allow" } ] } }  """)
            .ShouldContain("must start with '$.context.'");

    [Theory]
    [InlineData("""  "x-filterOperators": ["eq"]  """, "x-filterOperators")]
    [InlineData("""  "x-sortable": true  """, "x-sortable")]
    [InlineData("""  "x-encryption": { "type": "encrypt" }  """, "active x-encryption")]
    public void CoOccurrenceThatWouldLeakOrPreemptPrecedence_IsRejected(string sibling, string expected)
        => Only($$"""  "a": { "type": "string", {{sibling}}, "x-masking": { "operator": "mask" } }  """)
            .ShouldContain(expected);

    [Fact]
    public void ReplaceWithoutValue_IsRejected()
        => Only("""  "a": { "type": "string", "x-masking": { "operator": "replace" } }  """)
            .ShouldContain("requires params.value");

    [Theory]
    [InlineData("""{ "keepFirst": -1 }""", "non-negative integer")]
    [InlineData("""{ "keepLast": 1.5 }""", "non-negative integer")]
    [InlineData("""{ "maskingChar": "**" }""", "single character")]
    [InlineData("""{ "value": "x" }""", "not a parameter of this operator")]
    public void InvalidMaskParams_AreRejected(string parameters, string expected)
        => Only($$"""  "a": { "type": "string", "x-masking": { "operator": "mask", "params": {{parameters}} } }  """)
            .ShouldContain(expected);

    [Fact]
    public void UnknownMaskingKey_IsRejected()
        => Only("""  "a": { "type": "string", "x-masking": { "operator": "mask", "salt": "s3cr3t" } }  """)
            .ShouldContain("does not support 'salt'");

    [Fact]
    public void MaskingUnderItems_IsRejectedBecauseTheReadPathWouldIgnoreIt()
    {
        var errors = FieldMaskingDefinition.Validate(Schema("""
            "accounts": { "type": "array", "items": { "type": "object", "properties": {
                "iban": { "type": "string", "x-masking": { "operator": "mask" } } } } }
            """), "master");

        errors.Single().Message.ShouldContain("reachable through nested 'properties'");
    }

    [Fact]
    public void MaskingInsideDefaultOrExamples_IsNotMistakenForADeclaration()
        => FieldMaskingDefinition.Validate(Schema("""
            "a": { "type": "object", "default": { "x-masking": "literal" }, "examples": [ { "x-masking": 1 } ] }
            """), "master").ShouldBeEmpty();

    // ── x-encryption ─────────────────────────────────────────────────────────

    [Fact]
    public void ValidHashDeclaration_WithEveryNewField_Passes()
        => FieldMaskingDefinition.Validate(Schema("""
            "tckn": { "type": "string", "x-encryption": { "type": "hash", "params": { "algorithm": "sha512" },
                      "purpose": "PII-Identification", "redactInLogs": true, "retentionDays": 2555 } }
            """), "master").ShouldBeEmpty();

    [Fact]
    public void HashOnNonStringProperty_IsRejected()
        => Only("""  "a": { "type": "integer", "x-encryption": { "type": "hash" } }  """, keyword: "x-encryption")
            .ShouldContain("requires the property to declare type \"string\"");

    [Fact]
    public void SaltInTheSchema_IsRejected_BecauseTheMasterFunctionServesItVerbatim()
        => Only("""  "a": { "type": "string", "x-encryption": { "type": "hash", "params": { "salt": "0123456789abcdef" } } }  """, keyword: "x-encryption")
            .ShouldContain("never taken from a schema");

    [Fact]
    public void UnknownHashAlgorithm_IsRejected()
        => Only("""  "a": { "type": "string", "x-encryption": { "type": "hash", "params": { "algorithm": "md5" } } }  """, keyword: "x-encryption")
            .ShouldContain("'sha256' or 'sha512'");

    [Fact]
    public void HashWithFilterOperators_IsRejected()
        => Only("""  "a": { "type": "string", "x-filterOperators": ["eq"], "x-encryption": { "type": "hash" } }  """, keyword: "x-encryption")
            .ShouldContain("x-filterOperators");

    [Theory]
    [InlineData("encrypt")]
    [InlineData("none")]
    public void DenyGrantInEncryptionRoles_IsRejected(string type)
        => Only($$"""  "a": { "type": "string", "x-encryption": { "type": "{{type}}", "roles": [ { "role": "teller", "grant": "deny" } ] } }  """, keyword: "x-encryption")
            .ShouldContain("only grant 'allow' is accepted");

    /// <summary>Hashing happens on write and cannot be undone: nobody can be shown the raw value.</summary>
    [Fact]
    public void HashWithRoles_IsRejected()
        => Only("""  "a": { "type": "string", "x-encryption": { "type": "hash", "roles": [ { "role": "auditor", "grant": "allow" } ] } }  """, keyword: "x-encryption")
            .ShouldContain("not supported for type 'hash'");

    /// <summary>The stored value is the digest; a constraint written for the raw value would reject every later write.</summary>
    [Theory]
    [InlineData("\"pattern\": \"^[0-9]{11}$\"")]
    [InlineData("\"format\": \"email\"")]
    [InlineData("\"maxLength\": 11")]
    [InlineData("\"minLength\": 11")]
    [InlineData("\"enum\": [\"a\"]")]
    public void HashWithAValidationKeyword_IsRejected(string keyword)
        => Only($$"""  "a": { "type": "string", {{keyword}}, "x-encryption": { "type": "hash" } }  """, keyword: "x-encryption")
            .ShouldContain("cannot be combined with");

    [Theory]
    [InlineData("""  "purpose": ""  """, "purpose must be a non-empty string")]
    [InlineData("""  "redactInLogs": "yes"  """, "redactInLogs must be a boolean")]
    [InlineData("""  "retentionDays": 0  """, "retentionDays must be a positive integer")]
    public void InvalidMetadataFields_AreRejected(string field, string expected)
        => Only($$"""  "a": { "type": "string", "x-encryption": { "type": "none", {{field}} } }  """, keyword: "x-encryption")
            .ShouldContain(expected);

    [Fact]
    public void MaskingNextToHash_IsRejectedOnce_UnderXMasking()
        => Only("""  "a": { "type": "string", "x-masking": { "operator": "mask" }, "x-encryption": { "type": "hash" } }  """)
            .ShouldContain("declare one transform per field");
}
