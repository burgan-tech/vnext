using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Validators;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Domain.Tests.Definitions.Validators;

/// <summary>
/// Unit tests for WorkflowValidator
/// </summary>
public class WorkflowValidatorTests : DomainTestBase<DomainEntryPoint>
{
    private readonly WorkflowValidator _validator;

    public WorkflowValidatorTests()
    {
        _validator = new WorkflowValidator();
    }

    #region DefaultAutoTransition Validation Tests

    [Fact]
    public void Validate_ShouldPass_WhenStateHasSingleDefaultAutoTransition()
    {
        // Arrange
        var workflow = CreateWorkflowWithDefaultAutoTransition();

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        // Filter out unrelated validation errors (like labels) to focus on DefaultAutoTransition
        var defaultAutoErrors = result.ValidationErrors
            .Where(e => e.ErrorMessage!.Contains("DefaultAutoTransition") || 
                        e.ErrorMessage!.Contains("rule defined"))
            .ToList();
        defaultAutoErrors.ShouldBeEmpty($"Unexpected errors: {string.Join(", ", defaultAutoErrors.Select(e => e.ErrorMessage))}");
    }

    [Fact]
    public void Validate_ShouldFail_WhenStateHasMultipleDefaultAutoTransitions()
    {
        // Arrange
        var workflow = CreateWorkflowWithMultipleDefaultAutoTransitions();

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => 
            e.ErrorMessage!.Contains("at most one DefaultAutoTransition"));
    }

    [Fact]
    public void Validate_ShouldFail_WhenDefaultAutoTransitionHasNonAutomaticTrigger()
    {
        // Arrange
        var workflow = CreateWorkflowWithDefaultAutoTransitionAndManualTrigger();

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => 
            e.ErrorMessage!.Contains("must have TriggerType.Automatic"));
    }

    [Fact]
    public void Validate_ShouldPass_WhenDefaultAutoTransitionHasNoRule()
    {
        // Arrange
        var workflow = CreateWorkflowWithDefaultAutoTransitionWithoutRule();

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        // DefaultAutoTransition should not require a rule
        var ruleErrors = result.ValidationErrors
            .Where(e => e.ErrorMessage!.Contains("must have a rule defined"))
            .ToList();
        ruleErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldFail_WhenRegularAutoTransitionHasNoRule()
    {
        // Arrange
        var workflow = CreateWorkflowWithRegularAutoTransitionWithoutRule();

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => 
            e.ErrorMessage!.Contains("must have a rule defined"));
    }

    #endregion

    #region Helper Methods

    private WorkflowDefinition CreateWorkflowWithDefaultAutoTransition()
    {
        var json = """
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": [
                        {
                            "key": "auto-approve",
                            "target": "approved",
                            "triggerType": "automatic",
                            "rule": {"location": "inline", "code": "dHJ1ZQ=="}
                        },
                        {
                            "key": "default-pending",
                            "target": "pending",
                            "triggerType": "automatic",
                            "kind": "defaultAutoTransition"
                        }
                    ]
                },
                {
                    "key": "approved",
                    "stateType": "finish",
                    "labels": [{"label": "Approved", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "pending",
                    "stateType": "intermediate",
                    "labels": [{"label": "Pending", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "startTransition": {
                "key": "start",
                "target": "initial",
                "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """;

        return DeserializeWorkflow(json);
    }

    private WorkflowDefinition CreateWorkflowWithMultipleDefaultAutoTransitions()
    {
        var json = """
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": [
                        {
                            "key": "default-1",
                            "target": "approved",
                            "triggerType": "automatic",
                            "kind": "defaultAutoTransition"
                        },
                        {
                            "key": "default-2",
                            "target": "pending",
                            "triggerType": "automatic",
                            "kind": "defaultAutoTransition"
                        }
                    ]
                },
                {
                    "key": "approved",
                    "stateType": "finish",
                    "labels": [{"label": "Approved", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "pending",
                    "stateType": "intermediate",
                    "labels": [{"label": "Pending", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "startTransition": {
                "key": "start",
                "target": "initial",
                "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """;

        return DeserializeWorkflow(json);
    }

    private WorkflowDefinition CreateWorkflowWithDefaultAutoTransitionAndManualTrigger()
    {
        var json = """
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": [
                        {
                            "key": "invalid-default",
                            "target": "approved",
                            "triggerType": "manual",
                            "kind": "defaultAutoTransition",
                            "labels": [{"label": "Invalid", "language": "en"}]
                        }
                    ]
                },
                {
                    "key": "approved",
                    "stateType": "finish",
                    "labels": [{"label": "Approved", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "startTransition": {
                "key": "start",
                "target": "initial",
                "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """;

        return DeserializeWorkflow(json);
    }

    private WorkflowDefinition CreateWorkflowWithDefaultAutoTransitionWithoutRule()
    {
        // Same as CreateWorkflowWithDefaultAutoTransition - DefaultAutoTransition has no rule
        return CreateWorkflowWithDefaultAutoTransition();
    }

    private WorkflowDefinition CreateWorkflowWithRegularAutoTransitionWithoutRule()
    {
        var json = """
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": [
                        {
                            "key": "auto-no-rule",
                            "target": "approved",
                            "triggerType": "automatic",
                            "labels": [{"label": "Auto", "language": "en"}]
                        }
                    ]
                },
                {
                    "key": "approved",
                    "stateType": "finish",
                    "labels": [{"label": "Approved", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "startTransition": {
                "key": "start",
                "target": "initial",
                "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """;

        return DeserializeWorkflow(json);
    }

    private static WorkflowDefinition DeserializeWorkflow(string json)
    {
        var workflow = System.Text.Json.JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonSerializerConstants.JsonOptions)!;
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }

    #endregion
}

