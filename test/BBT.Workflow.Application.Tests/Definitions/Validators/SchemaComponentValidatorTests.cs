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
    [InlineData(null, false)]
    [InlineData("MASTER", false)]
    [InlineData("workflow", false)]
    public void RootType_ControlsIndexes_WithoutChangingOtherTypes(string? type, bool valid)
    {
        var input = JsonSerializer.Deserialize<PublishInput>(JsonSerializer.Serialize(new
        {
            type,
            attributes = new
            {
                type = "workflow",
                schema = new { type = "object", properties = new { amount = new { type = "number" } } }
            }
        }), JsonSerializerConstants.JsonOptions)!;
        var attributes = JsonDocument.Parse(input.Attributes.GetRawText().Replace(
            "\"type\":\"number\"", "\"type\":\"number\",\"x-indexed\":true")).RootElement;
        var result = _validator.Validate(attributes);
        SchemaComponentValidator.ValidateRootType(attributes, input.Type, result);
        result.IsValid.ShouldBe(valid);
        input.Attributes.GetProperty("type").GetString().ShouldBe("workflow");
        input.Attributes.GetProperty("schema").GetProperty("type").GetString().ShouldBe("object");
    }

    [Theory]
    [InlineData("master")]
    [InlineData("transition")]
    [InlineData("view")]
    [InlineData("function")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RootType_AllPurposesAllowUnindexedSchemas(string? type)
    {
        var attributes = JsonDocument.Parse("""{"type":"json-schema","schema":{"type":"object"}}""").RootElement;
        var result = _validator.Validate(attributes);
        SchemaComponentValidator.ValidateRootType(attributes, type, result);
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void NonMaster_RejectsFalseMetadata_ButDoesNotInspectExampleData()
    {
        var attributes = JsonDocument.Parse("""{"type":"view","schema":{"properties":{"nested":{"properties":{"value":{"type":"string","x-indexed":false}}}}}}""").RootElement;
        var result = _validator.Validate(attributes);
        SchemaComponentValidator.ValidateRootType(attributes, "view", result);
        result.IsValid.ShouldBeFalse();
        var example = JsonDocument.Parse("""{"schema":{"type":"object","examples":[{"x-indexed":true}]}}""").RootElement;
        var exampleResult = new ComponentValidationResult();
        SchemaComponentValidator.ValidateRootType(example, "view", exampleResult);
        exampleResult.IsValid.ShouldBeTrue();
    }
}
