using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Unit tests for ViewComponentValidator
/// </summary>
public class ViewComponentValidatorTests
{
    private readonly ViewComponentValidator _validator;

    public ViewComponentValidatorTests()
    {
        _validator = new ViewComponentValidator();
    }

    [Fact]
    public void CanHandle_ShouldReturnTrue_ForSysViews()
    {
        // Act
        var result = _validator.CanHandle(RuntimeSysSchemaInfo.Views);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_ShouldReturnFalse_ForOtherTypes()
    {
        // Assert
        _validator.CanHandle(RuntimeSysSchemaInfo.Flows).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Tasks).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Functions).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Schemas).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Extensions).ShouldBeFalse();
        _validator.CanHandle("unknown").ShouldBeFalse();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForValidView()
    {
        // Arrange
        var viewJson = """
        {
            "type": "J",
            "content": "{\"test\": \"content\"}",
            "display": "test-display"
        }
        """;
        var attributes = JsonDocument.Parse(viewJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnError_ForMissingContent()
    {
        // Arrange
        var viewJson = """
        {
            "type": "J",
            "display": "test-display"
        }
        """;
        var attributes = JsonDocument.Parse(viewJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("View.Content"));
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

    [Fact]
    public void Validate_ShouldReturnSuccess_ForJsonViewWithRenderer()
    {
        // Arrange
        var viewJson = """
        {
            "type": "json",
            "content": "{\"test\": \"content\"}",
            "display": "test-display",
            "renderer": "flutter"
        }
        """;
        var attributes = JsonDocument.Parse(viewJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForJsonViewWithoutRenderer()
    {
        // Arrange
        var viewJson = """
        {
            "type": "json",
            "content": "{\"test\": \"content\"}",
            "display": "test-display"
        }
        """;
        var attributes = JsonDocument.Parse(viewJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnError_ForNonJsonViewWithRenderer()
    {
        // Arrange
        var viewJson = """
        {
            "type": "html",
            "content": "<p>hello</p>",
            "display": "test-display",
            "renderer": "angular"
        }
        """;
        var attributes = JsonDocument.Parse(viewJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("View.Renderer"));
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForNonJsonViewWithoutRenderer()
    {
        // Arrange
        var viewJson = """
        {
            "type": "html",
            "content": "<p>hello</p>",
            "display": "test-display"
        }
        """;
        var attributes = JsonDocument.Parse(viewJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForJsonViewWithNullRenderer()
    {
        // Arrange
        var viewJson = """
        {
            "type": "json",
            "content": "{\"test\": \"content\"}",
            "display": "test-display",
            "renderer": null
        }
        """;
        var attributes = JsonDocument.Parse(viewJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }
}
