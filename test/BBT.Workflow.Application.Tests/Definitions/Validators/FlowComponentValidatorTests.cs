using System;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Unit tests for FlowComponentValidator
/// </summary>
public class FlowComponentValidatorTests
{
    // WorkflowValidator is a sealed-in-practice concrete class with a non-virtual Validate, so it
    // cannot be mocked; the delegation is pinned end to end against the real validator instead.
    private readonly FlowComponentValidator _validator = new(new WorkflowValidator());

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

    private const string ValidWorkflow = """
    {
        "type": "F",
        "labels": [{"label": "Test", "language": "en"}],
        "states": [
            {
                "key": "review",
                "stateType": "initial",
                "labels": [{"label": "Review", "language": "en"}],
                "transitions": [
                    { "key": "approve", "target": "done", "triggerType": "manual", "labels": [{"label": "Approve", "language": "en"}] }
                ]
            },
            { "key": "done", "stateType": "finish", "labels": [{"label": "Done", "language": "en"}], "transitions": [] }
        ],
        "startTransition": { "key": "start", "target": "review", "triggerType": "manual", "labels": [{"label": "Start", "language": "en"}] }
    }
    """;

    [Fact]
    public void Validate_ShouldDelegateToWorkflowValidator_ForValidWorkflow()
    {
        var attributes = JsonDocument.Parse(ValidWorkflow).RootElement;

        var result = _validator.Validate(attributes);

        result.IsValid.ShouldBeTrue(string.Join("; ", result.ValidationErrors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Validate_ShouldSurfaceWorkflowValidatorErrors_ForSemanticallyInvalidWorkflow()
    {
        // Deserializes cleanly; only WorkflowValidator can reject it (start transition targets no state).
        var attributes = JsonDocument.Parse(ValidWorkflow.Replace("\"target\": \"review\"", "\"target\": \"missing\""))
            .RootElement;

        var result = _validator.Validate(attributes);

        result.IsValid.ShouldBeFalse();
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
