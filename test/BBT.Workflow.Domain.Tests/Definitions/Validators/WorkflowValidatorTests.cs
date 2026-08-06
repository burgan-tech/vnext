using System;
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

    [Fact]
    public void Validate_ShouldFail_WhenDynamicExpressoRuleIsWhitespace()
    {
        var workflow = DeserializeWorkflow("""
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
                            "key": "auto-expresso",
                            "target": "approved",
                            "triggerType": "automatic",
                            "rule": {"location": "dynamicExpresso", "code": "   ", "encoding": "NAT"}
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
        """);
        var validator = new WorkflowValidator();
        var result = validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("Dynamic Expresso", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("non-empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldPass_WhenDynamicExpressoRuleIsValid()
    {
        var workflow = DeserializeWorkflow("""
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
                            "key": "auto-expresso",
                            "target": "approved",
                            "triggerType": "automatic",
                            "rule": {"location": "dynamicExpresso", "code": "context.Instance != null", "encoding": "NAT"}
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
        """);
        var validator = new WorkflowValidator();
        var result = validator.Validate(workflow);

        result.ValidationErrors.ShouldNotContain(e =>
            e.ErrorMessage!.Contains("Dynamic Expresso", StringComparison.Ordinal));
    }

    #endregion

    #region Timeout Mapping Validation Tests

    [Fact]
    public void Validate_ShouldPass_WhenTimeoutHasMappingAndStaticTimer()
    {
        var workflow = DeserializeWorkflow("""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "timeout": {
                "key": "$timeout",
                "target": "timed-out",
                "versionStrategy": "None",
                "timer": {"reset": "false", "duration": "PT1H"},
                "mapping": {"location": "inline", "code": "dHJ1ZQ=="}
            },
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "timed-out",
                    "stateType": "finish",
                    "labels": [{"label": "Timed Out", "language": "en"}],
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
        """);

        var result = _validator.Validate(workflow);

        var timeoutErrors = result.ValidationErrors
            .Where(e => e.ErrorMessage!.Contains("timeout mapping", StringComparison.OrdinalIgnoreCase) ||
                        e.ErrorMessage!.Contains("static timer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        timeoutErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldPass_WhenTimeoutHasStaticTimerOnly()
    {
        var workflow = DeserializeWorkflow("""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "timeout": {
                "key": "$timeout",
                "target": "timed-out",
                "versionStrategy": "None",
                "timer": {"reset": "false", "duration": "PT30M"}
            },
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "timed-out",
                    "stateType": "finish",
                    "labels": [{"label": "Timed Out", "language": "en"}],
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
        """);

        var result = _validator.Validate(workflow);

        var timeoutErrors = result.ValidationErrors
            .Where(e => e.ErrorMessage!.Contains("timeout", StringComparison.OrdinalIgnoreCase) &&
                        e.ErrorMessage!.Contains("timer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        timeoutErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldFail_WhenTimeoutHasMappingButNoTimer()
    {
        var workflow = DeserializeWorkflow("""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "timeout": {
                "key": "$timeout",
                "target": "timed-out",
                "versionStrategy": "None",
                "mapping": {"location": "inline", "code": "dHJ1ZQ=="}
            },
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "timed-out",
                    "stateType": "finish",
                    "labels": [{"label": "Timed Out", "language": "en"}],
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
        """);

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("static timer configuration is also required as fallback",
                StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region SharedTransition AvailableIn Validation Tests

    [Fact]
    public void Validate_ShouldPass_WhenSharedTransitionHasNoAvailableIn()
    {
        var workflow = DeserializeWorkflow("""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "review",
                    "stateType": "intermediate",
                    "labels": [{"label": "Review", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "cancelled",
                    "stateType": "finish",
                    "labels": [{"label": "Cancelled", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [
                {
                    "key": "cancel",
                    "target": "cancelled",
                    "triggerType": "manual",
                    "labels": [{"label": "Cancel", "language": "en"}]
                }
            ],
            "startTransition": {
                "key": "start",
                "target": "initial",
                "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """);

        var result = _validator.Validate(workflow);

        var availableInErrors = result.ValidationErrors
            .Where(e => e.ErrorMessage!.Contains("availableIn", StringComparison.OrdinalIgnoreCase) ||
                        e.ErrorMessage!.Contains("AvailableIn", StringComparison.Ordinal))
            .ToList();
        availableInErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldPass_WhenSharedTransitionHasValidAvailableIn()
    {
        var workflow = DeserializeWorkflow("""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "review",
                    "stateType": "intermediate",
                    "labels": [{"label": "Review", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "cancelled",
                    "stateType": "finish",
                    "labels": [{"label": "Cancelled", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [
                {
                    "key": "cancel",
                    "target": "cancelled",
                    "triggerType": "manual",
                    "availableIn": ["initial", "review"],
                    "labels": [{"label": "Cancel", "language": "en"}]
                }
            ],
            "startTransition": {
                "key": "start",
                "target": "initial",
                "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """);

        var result = _validator.Validate(workflow);

        var availableInErrors = result.ValidationErrors
            .Where(e => e.ErrorMessage!.Contains("availableIn", StringComparison.OrdinalIgnoreCase) ||
                        e.ErrorMessage!.Contains("AvailableIn", StringComparison.Ordinal))
            .ToList();
        availableInErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldFail_WhenSharedTransitionAvailableInReferencesInvalidState()
    {
        var workflow = DeserializeWorkflow("""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": []
                },
                {
                    "key": "cancelled",
                    "stateType": "finish",
                    "labels": [{"label": "Cancelled", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [
                {
                    "key": "cancel",
                    "target": "cancelled",
                    "triggerType": "manual",
                    "availableIn": ["non-existent-state"],
                    "labels": [{"label": "Cancel", "language": "en"}]
                }
            ],
            "startTransition": {
                "key": "start",
                "target": "initial",
                "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """);

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("availableIn", StringComparison.OrdinalIgnoreCase) &&
            e.ErrorMessage.Contains("non-existent-state", StringComparison.Ordinal));
    }

    #endregion

    #region State Alias Validation Tests

    [Fact]
    public void Validate_ShouldPass_WhenStateAliasIsComplete()
    {
        var workflow = BuildWorkflowWithStateAlias("""
            {
                "name": "Değerlendirme Aşamasında",
                "roles": [ { "role": "backoffice.operator", "grant": "allow" } ],
                "labels": [ { "label": "Operasyon İncelemesinde", "language": "tr" } ]
            }
            """);

        var result = _validator.Validate(workflow);

        var aliasErrors = result.ValidationErrors
            .Where(e => e.ErrorMessage!.Contains("Alias", StringComparison.Ordinal))
            .ToList();
        aliasErrors.ShouldBeEmpty($"Unexpected alias errors: {string.Join(", ", aliasErrors.Select(e => e.ErrorMessage))}");
    }

    [Fact]
    public void Validate_ShouldPass_WhenStateHasNoAlias()
    {
        // The default workflow template has no alias arrays — alias is optional.
        var workflow = CreateWorkflowWithDefaultAutoTransition();

        var result = _validator.Validate(workflow);

        result.ValidationErrors
            .ShouldNotContain(e => e.ErrorMessage!.Contains("Alias", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldFail_WhenStateAliasHasNoLabels()
    {
        var workflow = BuildWorkflowWithStateAlias("""
            {
                "name": "Değerlendirme Aşamasında",
                "roles": [ { "role": "backoffice.operator", "grant": "allow" } ],
                "labels": []
            }
            """);

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("at least one label", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("Değerlendirme Aşamasında", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldFail_WhenStateAliasHasNoRoles()
    {
        var workflow = BuildWorkflowWithStateAlias("""
            {
                "name": "Değerlendirme Aşamasında",
                "roles": [],
                "labels": [ { "label": "Operasyon İncelemesinde", "language": "tr" } ]
            }
            """);

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("at least one role", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("Değerlendirme Aşamasında", StringComparison.Ordinal));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Builds an otherwise-valid workflow whose intermediate "pending" state carries the supplied alias entry.
    /// </summary>
    private WorkflowDefinition BuildWorkflowWithStateAlias(string aliasEntryJson)
    {
        var json = $$"""
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
                            "key": "go-pending",
                            "target": "pending",
                            "triggerType": "manual",
                            "labels": [{"label": "Go", "language": "en"}]
                        }
                    ]
                },
                {
                    "key": "pending",
                    "stateType": "intermediate",
                    "labels": [{"label": "Pending", "language": "en"}],
                    "transitions": [],
                    "alias": [ {{aliasEntryJson}} ]
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

    #region State Notification Validation Tests

    [Fact]
    public void Validate_ShouldPass_WhenStateNotificationHasMappingAndStateType()
    {
        var workflow = DeserializeWorkflow(StateNotificationsWorkflowJson("""
            { "type": "state", "mapping": { "location": "inline", "code": "dHJ1ZQ==" } }
        """));

        var result = _validator.Validate(workflow);

        result.ValidationErrors.ShouldNotContain(e => e.ErrorMessage!.Contains("Notification at index"));
    }

    [Fact]
    public void Validate_ShouldFail_WhenStateNotificationHasNoMapping()
    {
        var workflow = DeserializeWorkflow(StateNotificationsWorkflowJson("""
            { "type": "state", "mapping": { "code": "" } }
        """));

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("must define a mapping", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldFail_WhenStateNotificationTypeIsCommand()
    {
        var workflow = DeserializeWorkflow(StateNotificationsWorkflowJson("""
            { "type": "command", "mapping": { "location": "inline", "code": "dHJ1ZQ==" } }
        """));

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("unsupported type", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("Command", StringComparison.Ordinal));
    }

    private static string StateNotificationsWorkflowJson(string notificationEntryJson) => $$"""
    {
        "type": "F",
        "labels": [{"label": "Test", "language": "en"}],
        "states": [
            {
                "key": "initial",
                "stateType": "initial",
                "labels": [{"label": "Initial", "language": "en"}],
                "transitions": [],
                "notifications": [ {{notificationEntryJson}} ]
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

    #endregion

    #region availableIn Validation Tests

    [Fact]
    public void Validate_ShouldFail_WhenAvailableInStateDoesNotExist()
    {
        var workflow = DeserializeWorkflow(AvailableInWorkflowJson("""[ "review", "no-such-state" ]"""));

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("does not match any state 'no-such-state'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldFail_WhenAvailableInListsSameStateTwice()
    {
        // FindAvailableIn takes the first match, so a second entry for the same state is dead — and if
        // it is the one carrying the role narrowing, the restriction silently never applies.
        var workflow = DeserializeWorkflow(AvailableInWorkflowJson("""
            [ "review", { "state": "review", "roles": [ { "role": "supervisor", "grant": "allow" } ] } ]
            """));

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("more than once", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("review", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldFail_WhenAvailableInRolesHaveMalformedDynamicRole()
    {
        var workflow = DeserializeWorkflow(AvailableInWorkflowJson("""
            [ { "state": "review", "roles": [ { "role": "$user.customer", "grant": "allow" } ] } ]
            """));

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("Dynamic role '$user.customer'", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("AvailableIn[review].Roles", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldPass_WhenAvailableInMixesBothFormsValidly()
    {
        var workflow = DeserializeWorkflow(AvailableInWorkflowJson("""
            [ "review",
              { "state": "pending", "roles": [ { "role": "backoffice.supervisor", "grant": "allow" },
                                               { "role": "$InstanceStarter", "grant": "deny" } ] } ]
            """));

        var result = _validator.Validate(workflow);

        result.ValidationErrors.ShouldNotContain(e =>
            e.ErrorMessage!.Contains("AvailableIn", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldFail_WhenWellKnownExitAvailableInStateDoesNotExist()
    {
        // availableIn on the well-known transitions was previously never validated at all.
        var workflow = DeserializeWorkflow(WellKnownTransitionsWorkflowJson(
            exitAvailableInJson: """[ "ghost-state" ]"""));

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("exit transition", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("ghost-state", StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds an otherwise-valid workflow whose shared transition carries the supplied availableIn list.
    /// </summary>
    private static string AvailableInWorkflowJson(string availableInJson) => $$"""
    {
        "type": "F",
        "labels": [{"label": "Test", "language": "en"}],
        "states": [
            { "key": "review", "stateType": "initial", "labels": [{"label": "Review", "language": "en"}], "transitions": [] },
            { "key": "pending", "stateType": "intermediate", "labels": [{"label": "Pending", "language": "en"}], "transitions": [] }
        ],
        "sharedTransitions": [
            {
                "key": "escalate",
                "target": "$self",
                "triggerType": "manual",
                "labels": [{"label": "Escalate", "language": "en"}],
                "availableIn": {{availableInJson}}
            }
        ],
        "startTransition": {
            "key": "start",
            "target": "review",
            "triggerType": "manual",
            "labels": [{"label": "Start", "language": "en"}]
        }
    }
    """;

    #endregion

    #region Well-Known Transition (updateData / exit) Validation Tests

    [Fact]
    public void Validate_ShouldFail_WhenExitTransitionHasInvalidDynamicRole()
    {
        var workflow = DeserializeWorkflow(WellKnownTransitionsWorkflowJson(
            exitRolesJson: """[ { "role": "$user.customer", "grant": "allow" } ]"""));

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("Dynamic role", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("Workflow.Exit.Roles", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldFail_WhenUpdateDataTransitionHasInvalidDynamicRole()
    {
        var workflow = DeserializeWorkflow(WellKnownTransitionsWorkflowJson(
            updateDataRolesJson: """[ { "role": "$role.$.context.", "grant": "allow" } ]"""));

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("Dynamic role", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("Workflow.UpdateData.Roles", StringComparison.Ordinal));
    }

    /// <summary>
    /// Static role names, all four predefined instance roles and well-formed dynamic roles must all
    /// pass. Only dynamic-role *intent* is validated — a static name is free-form.
    /// </summary>
    [Fact]
    public void Validate_ShouldPass_WhenExitAndUpdateDataRolesAreValid()
    {
        var workflow = DeserializeWorkflow(WellKnownTransitionsWorkflowJson(
            exitRolesJson: """
                [ { "role": "backoffice.operator", "grant": "allow" },
                  { "role": "$InstanceStarter", "grant": "allow" },
                  { "role": "$PreviousUser", "grant": "allow" },
                  { "role": "$InstanceBehalfOfStarter", "grant": "allow" },
                  { "role": "$PreviousBehalfOfUser", "grant": "allow" },
                  { "role": "$user.$.context.Instance.Data.ownerId", "grant": "allow" },
                  { "role": "$userBehalfOf.$.context.Instance.Data.behalfOfId", "grant": "allow" },
                  { "role": "$role.$.context.Instance.Data.requiredRole", "grant": "allow" },
                  { "role": "$user.$.context.Instance.Data.assignedUsers[*].userId", "grant": "allow" } ]
                """,
            updateDataRolesJson: """[ { "role": "backoffice.supervisor", "grant": "deny" } ]"""));

        var result = _validator.Validate(workflow);

        result.ValidationErrors.ShouldNotContain(e =>
            e.ErrorMessage!.Contains("Workflow.Exit", StringComparison.Ordinal) ||
            e.ErrorMessage.Contains("Workflow.UpdateData", StringComparison.Ordinal));
    }

    /// <summary>
    /// A case variant of the '$.context.' literal is rejected: DynamicRoleGrant.TryParse compares it
    /// with Ordinal, so the runtime would silently treat the grant as a static role name that can
    /// never match. Validation must not be more permissive than the parser.
    /// </summary>
    [Fact]
    public void Validate_ShouldFail_WhenDynamicRoleContextPrefixHasWrongCase()
    {
        var workflow = DeserializeWorkflow(WellKnownTransitionsWorkflowJson(
            exitRolesJson: """[ { "role": "$user.$.Context.Instance.Data.ownerId", "grant": "allow" } ]"""));

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("case-sensitive", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("Workflow.Exit.Roles", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same rule applies to every transition carrying roles, not just the well-known three —
    /// here a plain state transition.
    /// </summary>
    [Fact]
    public void Validate_ShouldFail_WhenStateTransitionHasMalformedDynamicRole()
    {
        var workflow = DeserializeWorkflow($$"""
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
                            "key": "submit",
                            "target": "initial",
                            "triggerType": "manual",
                            "labels": [{"label": "Submit", "language": "en"}],
                            "roles": [ { "role": "$user.customer", "grant": "allow" } ]
                        }
                    ]
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
        """);

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("Dynamic role '$user.customer'", StringComparison.Ordinal) &&
            e.ErrorMessage.Contains("States[initial].Transitions[submit].Roles", StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds an otherwise-valid workflow carrying well-known <c>exit</c> and <c>updateData</c>
    /// transitions with the supplied role grants.
    /// </summary>
    private static string WellKnownTransitionsWorkflowJson(
        string exitRolesJson = "[]",
        string updateDataRolesJson = "[]",
        string exitAvailableInJson = "[]") => $$"""
    {
        "type": "F",
        "labels": [{"label": "Test", "language": "en"}],
        "states": [
            {
                "key": "initial",
                "stateType": "initial",
                "labels": [{"label": "Initial", "language": "en"}],
                "transitions": []
            },
            {
                "key": "exited",
                "stateType": "finish",
                "labels": [{"label": "Exited", "language": "en"}],
                "transitions": []
            }
        ],
        "sharedTransitions": [],
        "startTransition": {
            "key": "start",
            "target": "initial",
            "triggerType": "manual",
            "labels": [{"label": "Start", "language": "en"}]
        },
        "exit": {
            "key": "exit",
            "target": "exited",
            "triggerType": "manual",
            "labels": [{"label": "Exit", "language": "en"}],
            "roles": {{exitRolesJson}},
            "availableIn": {{exitAvailableInJson}}
        },
        "updateData": {
            "key": "update-data",
            "target": "$self",
            "triggerType": "manual",
            "labels": [{"label": "Update Data", "language": "en"}],
            "roles": {{updateDataRolesJson}}
        }
    }
    """;

    #endregion
}

