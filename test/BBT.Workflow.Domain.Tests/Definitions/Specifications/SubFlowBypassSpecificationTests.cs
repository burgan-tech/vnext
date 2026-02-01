using System;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Unit tests for SubFlowBypassSpecification.
/// Tests bypass behavior when parent instance has active SubFlow.
/// </summary>
public class SubFlowBypassSpecificationTests
{
    private readonly SubFlowBypassSpecification _specification;

    public SubFlowBypassSpecificationTests()
    {
        _specification = new SubFlowBypassSpecification();
    }

    [Fact]
    public void Priority_ShouldBe20()
    {
        // Assert
        _specification.Priority.ShouldBe(20);
    }

    [Fact]
    public void IsApplicable_WhenHasActiveSubFlowIsTrue_ShouldReturnTrue()
    {
        // Arrange
        var context = CreateContext(hasActiveSubFlow: true);

        // Act
        var result = _specification.IsApplicable(context);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void IsApplicable_WhenHasActiveSubFlowIsFalse_ShouldReturnFalse()
    {
        // Arrange
        var context = CreateContext(hasActiveSubFlow: false);

        // Act
        var result = _specification.IsApplicable(context);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_ShouldAlwaysReturnSuccess()
    {
        // Arrange
        var context = CreateContext(hasActiveSubFlow: true);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    private static TransitionExecutionContext CreateContext(bool hasActiveSubFlow)
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "sys_flows", "test-key");

        if (hasActiveSubFlow)
        {
            // Add an active SubFlow correlation
            var correlation = InstanceCorrelation.Create(
                Guid.NewGuid(),
                instanceId,
                "test-state",
                Guid.NewGuid(),
                "S", // SubFlow type code
                "test-domain",
                "test-subflow",
                "1.0.0");

            instance.AddCorrelation(correlation);
        }

        var context = new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            Instance = instance
        };

        return context;
    }
}
