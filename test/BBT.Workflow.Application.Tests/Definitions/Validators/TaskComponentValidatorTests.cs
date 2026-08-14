using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Unit tests for TaskComponentValidator
/// </summary>
public class TaskComponentValidatorTests
{
    private readonly TaskComponentValidator _validator;

    public TaskComponentValidatorTests()
    {
        _validator = new TaskComponentValidator();
    }

    [Fact]
    public void CanHandle_ShouldReturnTrue_ForSysTasks()
    {
        // Act
        var result = _validator.CanHandle(RuntimeSysSchemaInfo.Tasks);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_ShouldReturnFalse_ForOtherTypes()
    {
        // Assert
        _validator.CanHandle(RuntimeSysSchemaInfo.Flows).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Views).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Functions).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Schemas).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Extensions).ShouldBeFalse();
        _validator.CanHandle("unknown").ShouldBeFalse();
    }

    /// <summary>
    /// Issue #399: the external (orchestrator-executed) HTTP task type publishes like any other —
    /// discriminator "21" is a known TaskType and the shared HTTP config shape deserializes.
    /// </summary>
    [Fact]
    public void Validate_ShouldReturnSuccess_ForExternalHttpTask()
    {
        // Arrange
        var taskJson = """
        {
            "type": "21",
            "config": {
                "url": "https://google.com",
                "method": "GET"
            }
        }
        """;
        var attributes = JsonDocument.Parse(taskJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForValidTask()
    {
        // Arrange
        var taskJson = """
        {
            "type": "6",
            "config": {
                "url": "https://example.com",
                "method": "GET"
            }
        }
        """;
        var attributes = JsonDocument.Parse(taskJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnError_ForInvalidTaskType()
    {
        // Arrange
        var taskJson = """
        {
            "type": "InvalidType",
            "config": {}
        }
        """;
        var attributes = JsonDocument.Parse(taskJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("WorkflowTask.Type"));
    }

    [Theory]
    [InlineData("sourceMapping", "CacheAsideTask.SourceMapping")]
    [InlineData("keyExpression", "CacheAsideTask.KeyExpression")]
    public void Validate_ShouldReturnError_WhenCacheAsideScriptSlotDeclaresOnlyLocation(
        string slot,
        string expectedMember)
    {
        // Arrange - CacheAside (type 18) is the only task whose own config carries script slots.
        var taskJson = $$"""
        {
            "type": "18",
            "config": {
                "key": "customer-profile",
                "storeName": "statestore",
                "sourceTask": {"key": "t", "domain": "d", "flow": "sys-tasks", "version": "1.0.0"},
                "{{slot}}": { "location": "./src/CacheMapping.csx" }
            }
        }
        """;
        var attributes = JsonDocument.Parse(taskJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains(expectedMember));
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_WhenCacheAsideScriptSlotsCarryCode()
    {
        // Arrange - "cmV0dXJuIHRydWU7" == "return true;"
        var taskJson = """
        {
            "type": "18",
            "config": {
                "key": "customer-profile",
                "storeName": "statestore",
                "sourceTask": {"key": "t", "domain": "d", "flow": "sys-tasks", "version": "1.0.0"},
                "sourceMapping": { "location": "./src/CacheMapping.csx", "code": "cmV0dXJuIHRydWU7" }
            }
        }
        """;
        var attributes = JsonDocument.Parse(taskJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
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
