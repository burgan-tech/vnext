using System;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Execution.Transitions;

/// <summary>
/// Unit tests for TransitionExecutionContextExtensions
/// Tests extension methods that enhance TransitionExecutionContext functionality
/// </summary>
public class TransitionExecutionContextExtensionsTests
{
    #region IsSelfTransition Tests

    [Fact]
    public void IsSelfTransition_WhenTargetEqualsCurrent_ShouldReturnTrue()
    {
        // Arrange
        var context = CreateContext(
            currentStateKey: "waiting",
            transitionTarget: "waiting");

        // Act
        var result = context.IsSelfTransition();

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void IsSelfTransition_WhenTargetDifferentFromCurrent_ShouldReturnFalse()
    {
        // Arrange
        var context = CreateContext(
            currentStateKey: "waiting",
            transitionTarget: "approved");

        // Act
        var result = context.IsSelfTransition();

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void IsSelfTransition_WhenTargetEqualsCurrent_CaseInsensitive_ShouldReturnTrue()
    {
        // Arrange
        var context = CreateContext(
            currentStateKey: "Waiting",
            transitionTarget: "WAITING");

        // Act
        var result = context.IsSelfTransition();

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void IsSelfTransition_WhenTransitionIsNull_ShouldReturnFalse()
    {
        // Arrange
        var context = CreateContextWithoutTransition(currentStateKey: "waiting");

        // Act
        var result = context.IsSelfTransition();

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void IsSelfTransition_WhenCurrentStateIsNull_ShouldReturnFalse()
    {
        // Arrange
        var context = CreateContextWithoutCurrentState(transitionTarget: "waiting");

        // Act
        var result = context.IsSelfTransition();

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void IsSelfTransition_WhenTargetIsSelfKeyword_ShouldReturnTrue()
    {
        // Arrange
        var context = CreateContext(
            currentStateKey: "waiting",
            transitionTarget: "$self");

        // Act
        var result = context.IsSelfTransition();

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void IsSelfTransition_WhenTargetIsSelfKeyword_CaseInsensitive_ShouldReturnTrue()
    {
        // Arrange
        var context = CreateContext(
            currentStateKey: "waiting",
            transitionTarget: "$SELF");

        // Act
        var result = context.IsSelfTransition();

        // Assert
        result.ShouldBeTrue();
    }

    #endregion

    #region Helper Methods

    private static TransitionExecutionContext CreateContext(
        string currentStateKey,
        string transitionTarget)
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = WorkflowFactory.CreateDefault();
        var instance = Instance.Create(instanceId, workflowKey);
        var state = StateFactory.CreateDefault(currentStateKey, StateType.Intermediate);
        var transition = TransitionFactory.CreateDefault(
            key: "self-transition",
            fromState: currentStateKey,
            toState: transitionTarget);

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "self-transition",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static TransitionExecutionContext CreateContextWithoutTransition(string currentStateKey)
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = WorkflowFactory.CreateDefault();
        var instance = Instance.Create(instanceId, workflowKey);
        var state = StateFactory.CreateDefault(currentStateKey, StateType.Intermediate);

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = null,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static TransitionExecutionContext CreateContextWithoutCurrentState(string transitionTarget)
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = WorkflowFactory.CreateDefault();
        var instance = Instance.Create(instanceId, workflowKey);
        var transition = TransitionFactory.CreateDefault(
            key: "test-transition",
            fromState: null,
            toState: transitionTarget);

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = null!,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    #endregion
}
