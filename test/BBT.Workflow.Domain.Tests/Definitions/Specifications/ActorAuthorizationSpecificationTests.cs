using System;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Unit tests for ActorAuthorizationSpecification.
/// Tests actor validation based on trigger type.
/// </summary>
public class ActorAuthorizationSpecificationTests
{
    private readonly ActorAuthorizationSpecification _specification;

    public ActorAuthorizationSpecificationTests()
    {
        _specification = new ActorAuthorizationSpecification();
    }

    [Fact]
    public void Priority_ShouldBe40()
    {
        // Assert
        _specification.Priority.ShouldBe(40);
    }

    [Fact]
    public void IsApplicable_ShouldAlwaysReturnTrue()
    {
        // Arrange
        var context = CreateContext(TriggerType.Manual, ExecutionActor.User);

        // Act
        var result = _specification.IsApplicable(context);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_ManualTrigger_WithUserActor_ShouldReturnSuccess()
    {
        // Arrange
        var context = CreateContext(TriggerType.Manual, ExecutionActor.User);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_ManualTrigger_WithSystemActor_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateContext(TriggerType.Manual, ExecutionActor.System);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Transition:100010");
    }

    [Fact]
    public void IsSatisfiedBy_AutomaticTrigger_WithSystemActor_ShouldReturnSuccess()
    {
        // Arrange
        var context = CreateContext(TriggerType.Automatic, ExecutionActor.System, chainDepth: 5);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_AutomaticTrigger_WithUserActor_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateContext(TriggerType.Automatic, ExecutionActor.User);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Transition:100010");
    }

    [Fact]
    public void IsSatisfiedBy_AutomaticTrigger_ExceedingChainDepth_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateContext(TriggerType.Automatic, ExecutionActor.System, chainDepth: 51);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Transition:100017");
    }

    [Fact]
    public void IsSatisfiedBy_ScheduledTrigger_WithSystemActor_ShouldReturnSuccess()
    {
        // Arrange
        var context = CreateContext(TriggerType.Scheduled, ExecutionActor.System);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_ScheduledTrigger_WithUserActor_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateContext(TriggerType.Scheduled, ExecutionActor.User);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Transition:100010");
    }

    [Fact]
    public void IsSatisfiedBy_EventTrigger_WithUserActor_ShouldReturnSuccess()
    {
        // Arrange
        var context = CreateContext(TriggerType.Event, ExecutionActor.User);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_EventTrigger_WithSystemActor_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateContext(TriggerType.Event, ExecutionActor.System);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Transition:100010");
    }

    private static TransitionExecutionContext CreateContext(
        TriggerType triggerType,
        ExecutionActor actor,
        int chainDepth = 0)
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "sys_flows", "1.0.0","test-key");

        var context = new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            Trigger = triggerType,
            Instance = instance
        };

        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, actor);

        if (chainDepth > 0)
        {
            typeof(TransitionExecutionContext)
                .GetProperty(nameof(TransitionExecutionContext.ChainDepth))!
                .SetValue(context, chainDepth);
        }

        return context;
    }
}
