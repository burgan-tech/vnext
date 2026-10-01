using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Unit tests for SchemaRolesParser (master schema field-level <c>x-roles</c> and <c>x-masking</c> vocabulary).
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
                    ""x-roles"": [
                        { ""role"": ""morph-idm.maker"", ""grant"": ""allow"" },
                        { ""role"": ""morph-idm.approver"", ""grant"": ""allow"" }
                    ]
                },
                ""internalNotes"": {
                    ""type"": ""string"",
                    ""x-roles"": [
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
                            ""x-roles"": [{ ""role"": ""admin"", ""grant"": ""allow"" }]
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
                    ""x-roles"": [
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

    [Fact]
    public void ParsePropertyRoles_WhenLegacyRolesKey_IsIgnored()
    {
        // The keyword is x-roles. A bare "roles" was the pre-release spelling; the fixtures above used it
        // for months after the parser moved on, which is why they were red on master.
        var schema = JsonDocument.Parse(@"{
            ""properties"": { ""a"": { ""type"": ""string"", ""roles"": [{ ""role"": ""r1"", ""grant"": ""allow"" }] } }
        }").RootElement;
        SchemaRolesParser.ParsePropertyRoles(schema).ShouldBeEmpty();
    }

    [Fact]
    public void ParseExposure_ReadsRolesAndMaskingInOneWalk()
    {
        var schema = JsonDocument.Parse(@"{
            ""properties"": {
                ""iban"": {
                    ""type"": ""string"",
                    ""x-masking"": { ""operator"": ""mask"", ""params"": { ""keepFirst"": 2, ""keepLast"": 4, ""maskingChar"": ""#"" },
                                    ""roles"": [ { ""role"": ""morph-idm.auditor"", ""grant"": ""allow"" } ] }
                },
                ""note"": { ""type"": ""string"", ""x-roles"": [ { ""role"": ""admin"", ""grant"": ""allow"" } ],
                            ""x-masking"": { ""operator"": ""replace"", ""params"": { ""value"": ""[hidden]"" } } },
                ""customer"": { ""type"": ""object"", ""properties"": {
                    ""tckn"": { ""type"": ""string"", ""x-masking"": { ""operator"": ""mask"" } } } }
            }
        }").RootElement;

        var result = SchemaRolesParser.ParseExposure(schema);

        result.PathRoleGrants.Keys.ShouldBe(["note"]);
        result.PathMaskRules.Count.ShouldBe(3);
        var iban = result.PathMaskRules["iban"];
        iban.Operator.ShouldBe(FieldMaskRule.MaskOperator);
        iban.KeepFirst.ShouldBe(2);
        iban.KeepLast.ShouldBe(4);
        iban.MaskingChar.ShouldBe("#");
        iban.ExemptRoles.Count.ShouldBe(1);
        iban.ExemptRoles[0].Role.ShouldBe("morph-idm.auditor");
        result.PathMaskRules["note"].Operator.ShouldBe(FieldMaskRule.ReplaceOperator);
        result.PathMaskRules["note"].ReplaceValue.ShouldBe("[hidden]");
        var tckn = result.PathMaskRules["customer.tckn"];
        (tckn.KeepFirst, tckn.KeepLast, tckn.MaskingChar).ShouldBe((0, 0, "*"));
    }

    [Theory]
    [InlineData(@"""x-masking"": ""mask""")]
    [InlineData(@"""x-masking"": { ""operator"": ""redact"" }")]
    [InlineData(@"""x-masking"": { }")]
    [InlineData(@"""x-masking"": { ""operator"": ""mask"", ""params"": { ""keepFirst"": -3, ""maskingChar"": ""ab"" } }")]
    public void ParseExposure_WhenDeclarationIsMalformed_FailsClosedToFullMask(string masking)
    {
        // The publish validator rejects all of these; if one ever reaches the runtime it must mask
        // everything rather than turn into a clear value.
        var schema = JsonDocument.Parse(@"{ ""properties"": { ""a"": { ""type"": ""string"", " + masking + " } } }").RootElement;

        var rule = SchemaRolesParser.ParseExposure(schema).PathMaskRules["a"];

        rule.Operator.ShouldBe(FieldMaskRule.MaskOperator);
        (rule.KeepFirst, rule.KeepLast, rule.MaskingChar).ShouldBe((0, 0, "*"));
    }

    [Fact]
    public void ParseExposure_DropsDenyGrantsFromTheExemptionList()
    {
        var schema = JsonDocument.Parse(@"{ ""properties"": { ""a"": { ""type"": ""string"",
            ""x-masking"": { ""operator"": ""mask"", ""roles"": [
                { ""role"": ""teller"", ""grant"": ""deny"" }, { ""role"": ""auditor"", ""grant"": ""allow"" } ] } } } }").RootElement;

        var rule = SchemaRolesParser.ParseExposure(schema).PathMaskRules["a"];

        rule.ExemptRoles.Count.ShouldBe(1);
        rule.ExemptRoles[0].Role.ShouldBe("auditor");
    }

    [Fact]
    public void ParseExposure_DoesNotDescendIntoItemsOrDefs()
    {
        var schema = JsonDocument.Parse(@"{ ""properties"": {
            ""accounts"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
                ""iban"": { ""type"": ""string"", ""x-masking"": { ""operator"": ""mask"" } } } } } },
            ""$defs"": { ""x"": { ""properties"": { ""y"": { ""type"": ""string"", ""x-masking"": { ""operator"": ""mask"" } } } } } }").RootElement;

        SchemaRolesParser.ParseExposure(schema).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ParseExposure_ReadsAHashEncryptionAsARule_AndIgnoresOtherEncryptionTypes()
    {
        var schema = JsonDocument.Parse(@"{ ""properties"": {
            ""tckn"": { ""type"": ""string"", ""x-encryption"": { ""type"": ""hash"", ""params"": { ""algorithm"": ""sha512"" },
                        ""roles"": [ { ""role"": ""auditor"", ""grant"": ""deny"" }, { ""role"": ""teller"", ""grant"": ""allow"" } ] } },
            ""email"": { ""type"": ""string"", ""x-encryption"": { ""type"": ""persisted"" } },
            ""phone"": { ""type"": ""string"", ""x-encryption"": { ""type"": ""none"" } } } }").RootElement;

        var rules = SchemaRolesParser.ParseExposure(schema).PathMaskRules;

        rules.Keys.ShouldBe(["tckn"]);
        SchemaRolesParser.ParseExposure(schema).EncryptPaths.ShouldBeEmpty();
        rules["tckn"].Operator.ShouldBe(FieldMaskRule.HashOperator);
        rules["tckn"].HashAlgorithm.ShouldBe(FieldMaskRule.Sha512);
        rules["tckn"].IsEncryption.ShouldBeTrue();
        rules["tckn"].ExemptRoles.ShouldBeEmpty(); // hashing is irreversible: a hash rule never exempts anyone
        SchemaRolesParser.ParseExposure(schema).HashPaths["tckn"].ShouldBe(FieldMaskRule.Sha512);
    }

    [Fact]
    public void ParseExposure_WhenBothTransformsAreDeclared_TheLaterStageWins()
    {
        var schema = JsonDocument.Parse(@"{ ""properties"": { ""a"": { ""type"": ""string"",
            ""x-masking"": { ""operator"": ""mask"" }, ""x-encryption"": { ""type"": ""hash"" } } } }").RootElement;

        SchemaRolesParser.ParseExposure(schema).PathMaskRules["a"].Operator.ShouldBe(FieldMaskRule.HashOperator);
    }

    [Fact]
    public void ParseExposure_ReadsEncrypt_AsAnAtRestRule_AndListsItsPaths()
    {
        var schema = JsonDocument.Parse(@"{ ""properties"": {
            ""customer"": { ""type"": ""object"", ""properties"": {
                ""email"": { ""type"": ""string"", ""x-encryption"": { ""type"": ""encrypt"",
                             ""roles"": [ { ""role"": ""auditor"", ""grant"": ""allow"" } ] } } } },
            ""tckn"": { ""type"": ""string"", ""x-encryption"": { ""type"": ""hash"" } } } }").RootElement;

        var exposure = SchemaRolesParser.ParseExposure(schema);

        exposure.PathMaskRules["customer.email"].Operator.ShouldBe(FieldMaskRule.EncryptOperator);
        exposure.PathMaskRules["customer.email"].IsAtRestEncryption.ShouldBeTrue();
        exposure.PathMaskRules["customer.email"].IsEncryption.ShouldBeTrue();
        exposure.PathMaskRules["customer.email"].ExemptRoles.Count.ShouldBe(1);
        exposure.PathMaskRules["tckn"].IsAtRestEncryption.ShouldBeFalse();
        exposure.EncryptPaths.ShouldBe(["customer.email"]);
    }

    /// <summary>
    /// Case is rejected at publish, tolerated here: a declaration that slipped past the validator must still
    /// encrypt (fail closed) rather than store the value in plaintext.
    /// </summary>
    [Fact]
    public void ParseExposure_ToleratesCase_SoAnUnvalidatedDeclarationFailsClosed()
    {
        var schema = JsonDocument.Parse(@"{ ""properties"": {
            ""a"": { ""type"": ""string"", ""x-encryption"": { ""type"": ""Encrypt"" } },
            ""b"": { ""type"": ""string"", ""x-encryption"": { ""type"": ""HASH"" } } } }").RootElement;

        var exposure = SchemaRolesParser.ParseExposure(schema);

        exposure.EncryptPaths.ShouldBe(["a"]);
        exposure.PathMaskRules["b"].Operator.ShouldBe(FieldMaskRule.HashOperator);
    }
}
