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

    [Fact]
    public void IsApplicable_WhenActiveSubFlowAndSharedTransitionWithEmptyAvailableIn_ShouldReturnFalse()
    {
        // Shared transition with empty AvailableIn is available in all states,
        // so bypass should NOT apply (transition executes on parent).
        var context = CreateContextWithSharedTransition(
            hasActiveSubFlow: true,
            transitionKey: "cancel",
            availableIn: null);

        var result = _specification.IsApplicable(context);

        result.ShouldBeFalse();
    }

    [Fact]
    public void IsApplicable_WhenActiveSubFlowAndSharedTransitionWithMatchingAvailableIn_ShouldReturnFalse()
    {
        var context = CreateContextWithSharedTransition(
            hasActiveSubFlow: true,
            transitionKey: "cancel",
            availableIn: ["test-state"]);

        var result = _specification.IsApplicable(context);

        result.ShouldBeFalse();
    }

    [Fact]
    public void IsApplicable_WhenActiveSubFlowAndSharedTransitionWithNonMatchingAvailableIn_ShouldReturnTrue()
    {
        // Shared transition is NOT available in current state, so bypass applies (forward to child).
        var context = CreateContextWithSharedTransition(
            hasActiveSubFlow: true,
            transitionKey: "cancel",
            availableIn: ["other-state"]);

        var result = _specification.IsApplicable(context);

        result.ShouldBeTrue();
    }

    private static TransitionExecutionContext CreateContext(bool hasActiveSubFlow)
    {
        var workflow = Workflow.Create();
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        workflow.SetType("F");

        var currentState = State.Create("test-state", StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(currentState);

        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "sys_flows", "1.0.0","test-key");

        if (hasActiveSubFlow)
        {
            var correlation = InstanceCorrelation.Create(
                Guid.NewGuid(),
                instanceId,
                "test-state",
                Guid.NewGuid(),
                "S",
                "test-domain",
                "test-subflow",
                "1.0.0");

            instance.AddCorrelation(correlation);
        }

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            Workflow = workflow,
            Current = currentState,
            Instance = instance
        };
    }

    private static TransitionExecutionContext CreateContextWithSharedTransition(
        bool hasActiveSubFlow,
        string transitionKey,
        string[]? availableIn)
    {
        var workflow = Workflow.Create();
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        workflow.SetType("F");

        var currentState = State.Create("test-state", StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(currentState);

        var targetState = State.Create("cancelled", StateType.Finish, StateSubType.Cancelled, "Patch");
        workflow.AddState(targetState);

        var sharedTransition = Transition.Create(transitionKey, null, "cancelled", TriggerType.Manual, "Patch");
        if (availableIn != null)
        {
            foreach (var state in availableIn)
            {
                sharedTransition.AddAvailableIn(state);
            }
        }
        workflow.AddSharedTransition(sharedTransition);

        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "sys_flows", "1.0.0", "test-key");

        if (hasActiveSubFlow)
        {
            var correlation = InstanceCorrelation.Create(
                Guid.NewGuid(),
                instanceId,
                "test-state",
                Guid.NewGuid(),
                "S",
                "test-domain",
                "test-subflow",
                "1.0.0");
            instance.AddCorrelation(correlation);
        }

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Workflow = workflow,
            Current = currentState,
            Transition = sharedTransition,
            Instance = instance
        };
    }
}
