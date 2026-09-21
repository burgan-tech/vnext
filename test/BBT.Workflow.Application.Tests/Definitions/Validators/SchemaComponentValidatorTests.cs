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
}
