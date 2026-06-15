using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Unit tests for FunctionComponentValidator
/// </summary>
public class FunctionComponentValidatorTests
{
    private readonly FunctionComponentValidator _validator;

    public FunctionComponentValidatorTests()
    {
        _validator = new FunctionComponentValidator();
    }

    [Fact]
    public void CanHandle_ShouldReturnTrue_ForSysFunctions()
    {
        // Act
        var result = _validator.CanHandle(RuntimeSysSchemaInfo.Functions);

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
        _validator.CanHandle(RuntimeSysSchemaInfo.Schemas).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Extensions).ShouldBeFalse();
        _validator.CanHandle("unknown").ShouldBeFalse();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForValidFunction()
    {
        // Arrange
        var functionJson = """
        {
            "scope": "F",
            "task": {
                "type": "6",
                "config": {
                    "url": "https://example.com",
                    "method": "GET"
                }
            }
        }
        """;
        var attributes = JsonDocument.Parse(functionJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnError_ForMissingTask()
    {
        // Arrange
        var functionJson = """
        {
            "scope": "F"
        }
        """;
        var attributes = JsonDocument.Parse(functionJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Function.Task"));
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
}
