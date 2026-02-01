using System;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Execution;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Unit tests for ResumeModeSpecification.
/// Tests bypass behavior for SubFlow resume scenarios.
/// </summary>
public class ResumeModeSpecificationTests
{
    private readonly ResumeModeSpecification _specification;

    public ResumeModeSpecificationTests()
    {
        _specification = new ResumeModeSpecification();
    }

    [Fact]
    public void Priority_ShouldBe10()
    {
        // Assert
        _specification.Priority.ShouldBe(10);
    }

    [Fact]
    public void IsApplicable_WhenIsSubFlowResumeIsTrue_ShouldReturnTrue()
    {
        // Arrange
        var context = CreateContext(isSubFlowResume: true);

        // Act
        var result = _specification.IsApplicable(context);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void IsApplicable_WhenIsSubFlowResumeIsFalse_ShouldReturnFalse()
    {
        // Arrange
        var context = CreateContext(isSubFlowResume: false);

        // Act
        var result = _specification.IsApplicable(context);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_ShouldAlwaysReturnSuccess()
    {
        // Arrange
        var context = CreateContext(isSubFlowResume: true);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    private static TransitionExecutionContext CreateContext(bool isSubFlowResume)
    {
        var context = new TransitionExecutionContext
        {
            InstanceId = Guid.NewGuid(),
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual
        };

        if (isSubFlowResume)
        {
            context.Directives.MarkAsSubFlowResume();
        }

        return context;
    }
}
