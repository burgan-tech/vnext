using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Unit tests for SchemaComponentValidator
/// </summary>
public class SchemaComponentValidatorTests
{
    private readonly SchemaComponentValidator _validator;

    public SchemaComponentValidatorTests()
    {
        _validator = new SchemaComponentValidator();
    }

    [Fact]
    public void CanHandle_ShouldReturnTrue_ForSysSchemas()
    {
        // Act
        var result = _validator.CanHandle(RuntimeSysSchemaInfo.Schemas);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_ShouldReturnFalse_ForOtherTypes()
    {
        // Assert
        _validator.CanHandle(RuntimeSysSchemaInfo.Flows).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Tasks).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Views).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Functions).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Extensions).ShouldBeFalse();
        _validator.CanHandle("unknown").ShouldBeFalse();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForValidSchema()
    {
        // Arrange
        var schemaJson = """
        {
            "type": "json-schema",
            "schema": {
                "type": "object",
                "properties": {
                    "name": { "type": "string" }
                }
            }
        }
        """;
        var attributes = JsonDocument.Parse(schemaJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldAccept_MissingType_WithoutWarning()
    {
        // attributes.type is an optional role: absent means "no role" (a schema serving more than one role).
        var attributes = JsonDocument.Parse("""{ "schema": { "type": "object" } }""").RootElement;

        var result = _validator.Validate(attributes);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("master")]
    [InlineData("transition")]
    [InlineData("component")]
    [InlineData("function")]
    [InlineData("")]
    public void Validate_ShouldAccept_RoleTypes_WithoutWarning(string type)
    {
        var attributes = JsonDocument.Parse($$"""{ "type": "{{type}}", "schema": { "type": "object" } }""").RootElement;

        var result = _validator.Validate(attributes);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("workflow")]
    [InlineData("schema")]
    [InlineData("Master")]
    public void Validate_ShouldWarn_ForLegacyType(string type)
    {
        // Legacy free-text values still publish this release; the warning names the roles to migrate to.
        var attributes = JsonDocument.Parse($$"""{ "type": "{{type}}", "schema": { "type": "object" } }""").RootElement;

        var result = _validator.Validate(attributes);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldHaveSingleItem().ErrorMessage!.ShouldContain($"attributes.type '{type}' is not a schema role");
    }

    [Fact]
    public void Validate_ShouldRefuse_FilterOperatorQueryTokens_NamingTheDeclaration()
    {
        var attributes = JsonDocument.Parse("""
        {
            "type": "master",
            "schema": { "type": "object", "properties": {
                "amount": { "type": "number", "x-filterOperators": ["eq", "ge"] } } }
        }
        """).RootElement;

        var result = _validator.Validate(attributes);

        result.IsValid.ShouldBeFalse();
        var error = result.ValidationErrors.ShouldHaveSingleItem();
        error.ErrorMessage.ShouldBe("Field 'amount': x-filterOperators value 'ge' is not a declaration name; declare 'gte'.");
        error.MemberNames.ShouldContain("schema.x-filterOperators");
    }

    [Fact]
    public void Validate_ShouldRefuse_RuntimeComputedOnATransitionSchema()
    {
        var attributes = JsonDocument.Parse("""
        {
            "type": "transition",
            "schema": { "type": "object", "properties": {
                "a": { "type": "number" }, "b": { "type": "number" },
                "total": { "type": "number", "x-computed": { "evaluatedBy": "runtime",
                           "fallback": { "arithmetic": { "op": "add", "fields": ["a", "b"] } } } } } }
        }
        """).RootElement;

        var result = _validator.Validate(attributes);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldHaveSingleItem().MemberNames.ShouldContain("schema.x-computed");
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("master")]
    public void Validate_ShouldAccept_ClientEncryption_WithoutAnEncryptionHost(string type)
    {
        // A client rule is accepted on any schema and never applied by the runtime, so a host without
        // write-time encryption accepts it (the CanEncrypt refusal only concerns runtime rules).
        var attributes = JsonDocument.Parse($$"""
        {
            "type": "{{type}}",
            "schema": { "type": "object", "properties": {
                "password": { "type": "string", "x-encryption": { "source": "client",
                    "key": "urn:client:context:read:device:memory:device.hpke.publicKey",
                    "algorithm": "HPKE-P256-SHA256-AES128GCM", "into": "passwordEnc", "envelope": "publicKey" } } } }
        }
        """).RootElement;

        var result = _validator.Validate(attributes);

        result.IsValid.ShouldBeTrue(string.Join(" | ", result.ValidationErrors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Validate_ShouldReturnError_ForMissingSchema()
    {
        // Arrange
        var schemaJson = """
        {
            "type": "json-schema"
        }
        """;
        var attributes = JsonDocument.Parse(schemaJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("SchemaDefinition.Schema"));
    }

    [Fact]
    public void Validate_ShouldReturnError_ForInvalidJson()
    {
        // Arrange
        var invalidJson = JsonDocument.Parse("\"not an object\"").RootElement;

        // Act
        var result = _validator.Validate(invalidJson);

        // Assert
        result.IsValid.ShouldBeFalse();
    }


    [Theory]
    [InlineData("master", true)]
    [InlineData("transition", false)]
    [InlineData("view", false)]
    [InlineData("function", false)]
    [InlineData("workflow", false)]
    [InlineData("custom-schema", false)]
    [InlineData("MASTER", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void AttributesType_ControlsIndexes(string? type, bool valid)
    {
        foreach (var indexed in new[] { true, false })
        {
            var attributes = JsonSerializer.SerializeToElement(new
            {
                type,
                schema = new { type = "object", properties = new
                {
                    amount = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["type"] = "number", ["x-indexed"] = indexed
                    }
                } }
            });
            _validator.Validate(attributes).IsValid.ShouldBe(valid);
            attributes.GetProperty("schema").GetProperty("type").GetString().ShouldBe("object");
        }
    }

    [Theory]
    [InlineData("workflow")]
    [InlineData("task")]
    [InlineData("headers")]
    [InlineData("json-schema")]
    [InlineData("custom-schema")]
    [InlineData("MASTER")]
    [InlineData("master")]
    public void AttributesType_RemainsFreeText_ForUnindexedSchemas(string type)
    {
        var attributes = JsonSerializer.SerializeToElement(new { type, schema = new { type = "object" } });
        _validator.Validate(attributes).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("master", "view", false)]
    [InlineData("view", "master", true)]
    public void RootType_DoesNotControlIndexValidation(string rootType, string attributeType, bool valid)
    {
        var json = """{"type":"ROOT","attributes":{"type":"ATTRIBUTE","schema":{"properties":{"amount":{"type":"number","x-indexed":true}}}}}"""
            .Replace("ROOT", rootType).Replace("ATTRIBUTE", attributeType);
        var input = JsonSerializer.Deserialize<PublishInput>(json, JsonSerializerConstants.JsonOptions)!;
        _validator.Validate(input.Attributes).IsValid.ShouldBe(valid);
    }

    [Fact]
    public void NonMaster_RejectsNestedMetadata_ButDoesNotInspectExampleData()
    {
        var attributes = JsonDocument.Parse("""{"type":"view","schema":{"properties":{"nested":{"properties":{"value":{"type":"string","x-indexed":false}}}}}}""").RootElement;
        _validator.Validate(attributes).IsValid.ShouldBeFalse();
        var example = JsonDocument.Parse("""{"type":"view","schema":{"type":"object","examples":[{"x-indexed":true}]}}""").RootElement;
        _validator.Validate(example).IsValid.ShouldBeTrue();
    }

    private static JsonElement MasterSchema(string properties) => JsonDocument.Parse(
        "{\"type\":\"master\",\"schema\":{\"type\":\"object\",\"properties\":{" + properties + "}}}").RootElement;

    [Fact]
    public void Validate_WhenXMaskingIsValid_ShouldPass()
    {
        var result = new SchemaComponentValidator(new BBT.Workflow.Authorization.FakeFieldMaskingEngine())
            .Validate(MasterSchema("\"iban\":{\"type\":\"string\",\"x-masking\":{\"operator\":\"mask\",\"params\":{\"keepLast\":4}}}"));

        result.IsValid.ShouldBeTrue(string.Join(" | ", result.ValidationErrors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Validate_WhenXMaskingUsesADenyGrant_ShouldFailUnderSchemaXMasking()
    {
        var result = _validator.Validate(MasterSchema(
            "\"iban\":{\"type\":\"string\",\"x-masking\":{\"operator\":\"mask\",\"roles\":[{\"role\":\"teller\",\"grant\":\"deny\"}]}}"));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("schema.x-masking") && e.ErrorMessage!.Contains("only grant 'allow'"));
    }

    [Fact]
    public void Validate_WhenEngineRejectsTheRule_ShouldFailUnderSchemaXMasking()
    {
        var engine = NSubstitute.Substitute.For<BBT.Workflow.Authorization.IFieldMaskingEngine>();
        NSubstitute.SubstituteExtensions.Returns(
            engine.Validate(NSubstitute.Arg.Any<BBT.Workflow.Definitions.Schemas.FieldMaskRule>()),
            (System.Collections.Generic.IReadOnlyList<string>)new[] { "engine says no" });

        var result = new SchemaComponentValidator(engine)
            .Validate(MasterSchema("\"iban\":{\"type\":\"string\",\"x-masking\":{\"operator\":\"mask\"}}"));

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage == "Field 'iban': engine says no");
    }

    private sealed class EncryptionStatus(bool canEncrypt) : BBT.Workflow.Authorization.IFieldEncryptionStatus
    {
        public bool CanEncrypt => canEncrypt;
    }

    private const string EncryptedEmail = "\"email\":{\"type\":\"string\",\"x-encryption\":{\"type\":\"encrypt\"}}";

    [Fact]
    public void Validate_WhenEncryptIsDeclaredAndTheHostCanEncrypt_ShouldPass()
    {
        var result = new SchemaComponentValidator(new BBT.Workflow.Authorization.FakeFieldMaskingEngine(), new EncryptionStatus(true))
            .Validate(MasterSchema(EncryptedEmail));

        result.IsValid.ShouldBeTrue(string.Join(" | ", result.ValidationErrors.Select(e => e.ErrorMessage)));
    }

    /// <summary>
    /// Without an active key (or with EncryptWrites off) the value would be stored in plaintext; the publish is the
    /// only point where that is visible, so it is refused there.
    /// </summary>
    [Fact]
    public void Validate_WhenEncryptIsDeclaredAndTheHostCannotEncrypt_ShouldFailUnderSchemaXEncryption()
    {
        foreach (var validator in new[]
                 {
                     new SchemaComponentValidator(new BBT.Workflow.Authorization.FakeFieldMaskingEngine(), new EncryptionStatus(false)),
                     new SchemaComponentValidator(new BBT.Workflow.Authorization.FakeFieldMaskingEngine()),
                 })
        {
            var result = validator.Validate(MasterSchema(EncryptedEmail));

            result.IsValid.ShouldBeFalse();
            result.ValidationErrors.ShouldContain(e =>
                e.MemberNames.Contains("schema.x-encryption") && e.ErrorMessage!.Contains("stored in plaintext"));
        }
    }

    [Fact]
    public void Validate_WhenARemovedEncryptionTypeIsDeclared_ShouldFailNamingTheReplacement()
    {
        var result = _validator.Validate(MasterSchema("\"email\":{\"type\":\"string\",\"x-encryption\":{\"type\":\"persisted\"}}"));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("use 'encrypt'"));
    }

    [Theory]
    [InlineData("$user.$CreatedBy")]
    [InlineData("$user.$context.Instance.Data.customerId")]
    public void Validate_WhenXRolesHasAnInvalidDynamicPath_ShouldFailUnderSchemaXRoles(string role)
    {
        var result = _validator.Validate(MasterSchema(
            "\"a\":{\"type\":\"string\",\"x-roles\":[{\"role\":\"" + role + "\",\"grant\":\"allow\"}]}"));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("schema.x-roles") && e.ErrorMessage!.Contains(role));
    }

    [Fact]
    public void Validate_WhenXRolesCombinatorLeafIsInvalid_ShouldFailNamingTheLeaf()
    {
        var result = _validator.Validate(MasterSchema(
            "\"a\":{\"type\":\"string\",\"x-roles\":[{\"grant\":\"deny\",\"allOf\":[{\"role\":\"ops\"},{\"role\":\"$user.$CreatedBy\"}]}]}"));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("$user.$CreatedBy"));
    }

    [Fact]
    public void Validate_WhenXRolesCombinatorIsValid_ShouldPass()
    {
        var result = _validator.Validate(MasterSchema(
            "\"a\":{\"type\":\"string\",\"x-roles\":[{\"grant\":\"allow\",\"anyOf\":[{\"role\":\"$InstanceStarter\"},{\"role\":\"$InstanceBehalfOfStarter\"}]}," +
            "{\"grant\":\"deny\",\"allOf\":[{\"role\":\"ops\"},{\"role\":\"$user.$.context.Instance.Data.customerId\"}]}]}"));

        result.IsValid.ShouldBeTrue(string.Join(" | ", result.ValidationErrors.Select(e => e.ErrorMessage)));
    }

    [Theory]
    [InlineData("""{"grant":"allow","role":"a","allOf":[{"role":"b"}]}""")]
    [InlineData("""{"grant":"allow","allOf":[{"role":"a"}],"anyOf":[{"role":"b"}]}""")]
    [InlineData("""{"grant":"allow","anyOf":[{"role":"a","grant":"allow"}]}""")]
    [InlineData("""{"grant":"allow"}""")]
    public void Validate_WhenXRolesEntryHasABadShape_ShouldFailUnderSchemaXRoles(string entry)
    {
        var result = _validator.Validate(MasterSchema(
            "\"a\":{\"type\":\"string\",\"x-roles\":[" + entry + "]}"));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("schema.x-roles"));
    }

    [Fact]
    public void Validate_WhenXMaskingExemptionUsesACombinator_ShouldFailWithTheExemptionRule()
    {
        var result = _validator.Validate(MasterSchema(
            "\"iban\":{\"type\":\"string\",\"x-masking\":{\"operator\":\"mask\",\"roles\":[{\"grant\":\"allow\",\"anyOf\":[{\"role\":\"a\"}]}]}}"));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.MemberNames.Contains("schema.x-masking.roles") && e.ErrorMessage!.Contains("exemption list"));
    }

    [Fact]
    public void Validate_WhenXEncryptionExemptionUsesACombinator_ShouldFailWithTheExemptionRule()
    {
        var result = _validator.Validate(MasterSchema(
            "\"e\":{\"type\":\"string\",\"x-encryption\":{\"type\":\"encrypt\",\"roles\":[{\"grant\":\"allow\",\"allOf\":[{\"role\":\"a\"}]}]}}"));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.MemberNames.Contains("schema.x-encryption.roles") && e.ErrorMessage!.Contains("exemption list"));
    }
}
