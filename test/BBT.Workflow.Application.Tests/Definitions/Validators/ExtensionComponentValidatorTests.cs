using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Unit tests for ExtensionComponentValidator
/// </summary>
public class ExtensionComponentValidatorTests
{
    private readonly ExtensionComponentValidator _validator;

    public ExtensionComponentValidatorTests()
    {
        _validator = new ExtensionComponentValidator();
    }

    [Fact]
    public void CanHandle_ShouldReturnTrue_ForSysExtensions()
    {
        // Act
        var result = _validator.CanHandle(RuntimeSysSchemaInfo.Extensions);

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
        _validator.CanHandle(RuntimeSysSchemaInfo.Schemas).ShouldBeFalse();
        _validator.CanHandle("unknown").ShouldBeFalse();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForValidExtension()
    {
        // Arrange
        var extensionJson = """
        {
            "type": "O",
            "scope": "O",
            "task": {
                "type": "6",
                "config": {
                    "url": "https://example.com",
                    "method": "GET"
                }
            }
        }
        """;
        var attributes = JsonDocument.Parse(extensionJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnError_WhenTaskMappingDeclaresOnlyLocation()
    {
        // Arrange - the build step never inlined the .csx body, so the mapping would be silently skipped.
        var extensionJson = """
        {
            "type": "global",
            "scope": "everywhere",
            "task": {
                "order": 1,
                "task": {"key": "t", "domain": "d", "flow": "sys-tasks", "version": "1.0.0"},
                "mapping": { "location": "./src/EnrichMapping.csx" }
            }
        }
        """;
        var attributes = JsonDocument.Parse(extensionJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Extension.Task.Mapping"));
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_WhenTaskMappingCarriesCode()
    {
        // Arrange - "cmV0dXJuIHRydWU7" == "return true;"
        var extensionJson = """
        {
            "type": "global",
            "scope": "everywhere",
            "task": {
                "order": 1,
                "task": {"key": "t", "domain": "d", "flow": "sys-tasks", "version": "1.0.0"},
                "mapping": { "location": "./src/EnrichMapping.csx", "code": "cmV0dXJuIHRydWU7" }
            }
        }
        """;
        var attributes = JsonDocument.Parse(extensionJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnError_ForMissingTask()
    {
        // Arrange
        var extensionJson = """
        {
            "type": "O",
            "scope": "O"
        }
        """;
        var attributes = JsonDocument.Parse(extensionJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Extension.Task"));
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
