using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Unit tests for <see cref="SensitiveSchemaParser"/>. <c>Parse</c> is the lenient runtime path,
/// <c>Validate</c> is the strict publish-time path — the tests pin both tempers.
/// </summary>
public sealed class SensitiveSchemaParserTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    #region Parse

    [Fact]
    public void Parse_WhenNothingAnnotated_ReturnsEmpty()
        => SensitiveSchemaParser.Parse(Json("""{ "properties": { "a": { "type": "string" } } }"""))
            .ShouldBeEmpty();

    [Fact]
    public void Parse_ReadsTheFullAnnotation()
    {
        var fields = SensitiveSchemaParser.Parse(Json("""
        {
          "properties": {
            "ssn": {
              "type": "string",
              "x-sensitive": {
                "enabled": true,
                "purpose": "PII-Identification",
                "encryptAtRest": true,
                "redactInLogs": true,
                "maskingPattern": "***-**-{last4}",
                "retentionDays": 2555
              }
            }
          }
        }
        """));

        var ssn = fields["ssn"];
        ssn.Enabled.ShouldBeTrue();
        ssn.Purpose.ShouldBe("PII-Identification");
        ssn.EncryptAtRest.ShouldBeTrue();
        ssn.RedactInLogs.ShouldBeTrue();
        ssn.MaskingPattern.ShouldBe("***-**-{last4}");
        ssn.RetentionDays.ShouldBe(2555);
        ssn.HasProtection.ShouldBeTrue();
    }

    [Fact]
    public void Parse_OmitsDisabledAnnotations()
        => SensitiveSchemaParser.Parse(Json("""
            { "properties": { "a": { "x-sensitive": { "enabled": false, "redactInLogs": true } } } }
            """))
            .ShouldBeEmpty();

    [Fact]
    public void Parse_OmitsMalformedAnnotationsInsteadOfThrowing()
    {
        // Lenient on purpose: a malformed annotation must not fail a live transition. Validate()
        // is what refuses to publish it.
        SensitiveSchemaParser.Parse(Json("""
            { "properties": { "a": { "x-sensitive": "yes" }, "b": { "x-sensitive": [] } } }
            """))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Parse_ReachesArrayItemProperties()
        => SensitiveSchemaParser.Parse(Json("""
            {
              "properties": {
                "cards": {
                  "items": {
                    "properties": {
                      "pan": { "type": "string", "x-sensitive": { "enabled": true, "purpose": "PCI" } }
                    }
                  }
                }
              }
            }
            """))
            .Keys.ShouldContain("cards[].pan");

    #endregion

    #region Validate

    private static string[] Validate(string json)
        => SensitiveSchemaParser.Validate(Json(json)).Select(problem => problem.Message).ToArray();

    [Fact]
    public void Validate_WhenSound_ReportsNothing()
        => Validate("""
            {
              "properties": {
                "email": {
                  "type": "string",
                  "x-sensitive": {
                    "enabled": true, "purpose": "PII",
                    "encryptAtRest": true, "redactInLogs": true,
                    "maskingPattern": "{first}***@***.***"
                  }
                }
              }
            }
            """).ShouldBeEmpty();

    [Fact]
    public void Validate_RejectsEncryptedAndFilterable()
    {
        // The core conflict: filtering is raw SQL over the Data jsonb, so a predicate on an
        // encrypted path matches nothing and reports no error. Publish time is the only place
        // this is visible.
        var problems = Validate("""
            {
              "properties": {
                "email": {
                  "type": "string",
                  "x-filterOperators": ["eq"],
                  "x-sensitive": { "enabled": true, "purpose": "PII", "encryptAtRest": true }
                }
              }
            }
            """);

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("x-filterOperators");
    }

    [Fact]
    public void Validate_RejectsEncryptedAndSortable()
    {
        var problems = Validate("""
            {
              "properties": {
                "email": {
                  "type": "string",
                  "x-sortable": true,
                  "x-sensitive": { "enabled": true, "purpose": "PII", "encryptAtRest": true }
                }
              }
            }
            """);

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("x-sortable");
    }

    [Fact]
    public void Validate_AllowsFilterableWhenOnlyRedacting()
    {
        // redactInLogs does not touch storage, so filtering stays perfectly valid.
        Validate("""
            {
              "properties": {
                "email": {
                  "type": "string",
                  "x-filterOperators": ["eq"],
                  "x-sortable": true,
                  "x-sensitive": { "enabled": true, "purpose": "PII", "redactInLogs": true }
                }
              }
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RejectsEncryptionOnNonStringTypes()
    {
        var problems = Validate("""
            {
              "properties": {
                "amount": {
                  "type": "number",
                  "x-sensitive": { "enabled": true, "purpose": "Financial", "encryptAtRest": true }
                }
              }
            }
            """);

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("type: string");
    }

    [Fact]
    public void Validate_RequiresPurpose()
    {
        var problems = Validate("""
            { "properties": { "email": { "type": "string", "x-sensitive": { "enabled": true } } } }
            """);

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("purpose");
    }

    [Fact]
    public void Validate_RejectsProtectionFlagsWhileDisabled()
    {
        // The "I set encryptAtRest and forgot enabled" bug: the author believes the field is
        // protected and nothing protects it.
        var problems = Validate("""
            {
              "properties": {
                "email": { "type": "string", "x-sensitive": { "enabled": false, "encryptAtRest": true } }
              }
            }
            """);

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("NOT be protected");
    }

    [Fact]
    public void Validate_AcceptsFullyDisabledAnnotationAsStaging()
        => Validate("""
            { "properties": { "email": { "type": "string", "x-sensitive": { "enabled": false } } } }
            """).ShouldBeEmpty();

    [Fact]
    public void Validate_RejectsUnknownMaskingToken()
    {
        var problems = Validate("""
            {
              "properties": {
                "email": {
                  "type": "string",
                  "x-sensitive": { "enabled": true, "purpose": "PII", "maskingPattern": "{middle}" }
                }
              }
            }
            """);

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("maskingPattern");
    }

    [Fact]
    public void Validate_RejectsNonPositiveRetention()
    {
        var problems = Validate("""
            {
              "properties": {
                "email": {
                  "type": "string",
                  "x-sensitive": { "enabled": true, "purpose": "PII", "retentionDays": 0 }
                }
              }
            }
            """);

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("retentionDays");
    }

    [Fact]
    public void Validate_RejectsNonObjectAnnotation()
    {
        var problems = Validate("""{ "properties": { "email": { "x-sensitive": true } } }""");

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("must be an object");
    }

    [Fact]
    public void Validate_RejectsAnnotationsTheRuntimeCanNeverReach()
    {
        var problems = Validate("""
            {
              "$defs": {
                "address": {
                  "properties": { "zip": { "x-sensitive": { "enabled": true, "purpose": "PII" } } }
                }
              },
              "properties": { "shipping": { "$ref": "#/$defs/address" } }
            }
            """);

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain("never applied");
    }

    #endregion

    #region Cache

    [Fact]
    public void SensitiveSchemaCache_ParsesOncePerIdentity()
    {
        SensitiveSchemaCache.Clear();

        var annotated = Json("""
            { "properties": { "a": { "x-sensitive": { "enabled": true, "purpose": "PII" } } } }
            """);
        var first = SensitiveSchemaCache.GetOrParse("core", "master", "1.0.0", annotated);

        // Same identity, different body: the cached parse must win, proving no re-parse happened.
        var second = SensitiveSchemaCache.GetOrParse("core", "master", "1.0.0", Json("{}"));

        second.ShouldBeSameAs(first);
        second.Keys.ShouldContain("a");

        // A new version is a new identity.
        SensitiveSchemaCache.GetOrParse("core", "master", "1.0.1", Json("{}")).ShouldBeEmpty();

        SensitiveSchemaCache.Clear();
    }

    #endregion
}
