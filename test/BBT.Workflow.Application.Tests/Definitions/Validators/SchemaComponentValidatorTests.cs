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
    public void Validate_ShouldReturnError_ForMissingType()
    {
        // Arrange
        var schemaJson = """
        {
            "schema": {
                "type": "object"
            }
        }
        """;
        var attributes = JsonDocument.Parse(schemaJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("SchemaDefinition.Type"));
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
}
