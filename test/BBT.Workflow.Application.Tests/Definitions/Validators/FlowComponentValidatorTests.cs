using System;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Unit tests for FlowComponentValidator
/// </summary>
public class FlowComponentValidatorTests
{
    private readonly Mock<WorkflowValidator> _mockWorkflowValidator;
    private readonly FlowComponentValidator _validator;

    public FlowComponentValidatorTests()
    {
        _mockWorkflowValidator = new Mock<WorkflowValidator>();
        _validator = new FlowComponentValidator(_mockWorkflowValidator.Object);
    }

    [Fact]
    public void CanHandle_ShouldReturnTrue_ForSysFlows()
    {
        // Act
        var result = _validator.CanHandle(RuntimeSysSchemaInfo.Flows);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_ShouldReturnFalse_ForOtherTypes()
    {
        // Assert
        _validator.CanHandle(RuntimeSysSchemaInfo.Tasks).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Views).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Functions).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Schemas).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Extensions).ShouldBeFalse();
        _validator.CanHandle("unknown").ShouldBeFalse();
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
    public void Validate_ShouldDelegateToWorkflowValidator_ForValidWorkflow()
    {
        // Arrange
        var workflowJson = """
        {
            "key": "test-flow",
            "domain": "test-domain",
            "version": "1.0.0",
            "flow": "sys-flows",
            "type": "F",
            "states": [
                { "key": "initial", "stateType": "I" },
                { "key": "completed", "stateType": "C" }
            ],
            "startTransition": {
                "key": "start",
                "target": "initial"
            }
        }
        """;
        var attributes = JsonDocument.Parse(workflowJson).RootElement;

        var workflowValidationResult = new WorkflowValidationResult();
        _mockWorkflowValidator.Setup(v => v.Validate(It.IsAny<Workflow>()))
            .Returns(workflowValidationResult);

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
        _mockWorkflowValidator.Verify(v => v.Validate(It.IsAny<Workflow>()), Times.Once);
    }

    private static string WorkflowWithQueryRoles(string queryRolesJson) => $$"""
    {
        "type": "F",
        "labels": [{"label": "Test", "language": "en"}],
        "states": [
            { "key": "review", "stateType": "initial", "labels": [{"label": "Review", "language": "en"}], "transitions": [] }
        ],
        "queryRoles": {{queryRolesJson}},
        "startTransition": { "key": "start", "target": "review", "triggerType": "manual", "labels": [{"label": "Start", "language": "en"}] }
    }
    """;

    [Theory]
    [InlineData("""[{"grant":"allow","role":"a","allOf":[{"role":"b"}]}]""")]
    [InlineData("""[{"grant":"allow","allOf":[]}]""")]
    [InlineData("""[{"grant":"allow"}]""")]
    public void Validate_ShouldReturnValidationError_WhenRoleGrantShapeIsMalformed(string queryRoles)
    {
        var validator = new FlowComponentValidator(new WorkflowValidator());
        var attributes = JsonDocument.Parse(WorkflowWithQueryRoles(queryRoles)).RootElement;

        var result = validator.Validate(attributes);

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Validate_ShouldReportExactlyOneOf_WhenRoleAndAllOfCombined()
    {
        var validator = new FlowComponentValidator(new WorkflowValidator());
        var attributes = JsonDocument.Parse(
            WorkflowWithQueryRoles("""[{"grant":"allow","role":"a","allOf":[{"role":"b"}]}]""")).RootElement;

        var result = validator.Validate(attributes);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("exactly one of 'role', 'allOf' or 'anyOf'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""[{"grant":"allow","allOf":[{"role":"a","grant":"allow"}]}]""")]
    [InlineData("""[{"grant":"allow","allOf":[{"role":"a","allOf":[{"role":"b"}]}]}]""")]
    public void Validate_ShouldReturnValidationError_WhenCombinatorChildCarriesExtraMembers(string queryRoles)
    {
        var validator = new FlowComponentValidator(new WorkflowValidator());
        var attributes = JsonDocument.Parse(WorkflowWithQueryRoles(queryRoles)).RootElement;

        var result = validator.Validate(attributes);

        result.IsValid.ShouldBeFalse();
    }
}
