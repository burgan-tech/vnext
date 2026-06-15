using System;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Unit tests for SharedTransitionAvailabilitySpecification.
/// Validates AvailableIn semantics: empty/null means available in all states,
/// populated list restricts availability to listed states.
/// </summary>
public class SharedTransitionAvailabilitySpecificationTests
{
    private readonly SharedTransitionAvailabilitySpecification _specification;

    public SharedTransitionAvailabilitySpecificationTests()
    {
        _specification = new SharedTransitionAvailabilitySpecification();
    }

    [Fact]
    public void Priority_ShouldBe60()
    {
        _specification.Priority.ShouldBe(60);
    }

    #region IsApplicable

    [Fact]
    public void IsApplicable_ShouldReturnTrue_WhenTransitionIsSharedTransition()
    {
        var context = CreateContextWithSharedTransition("cancel", "cancelled", "review");

        var result = _specification.IsApplicable(context);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsApplicable_ShouldReturnFalse_WhenTransitionIsNotSharedTransition()
    {
        var context = CreateContextWithStateTransition("submit", "review", "approved");

        var result = _specification.IsApplicable(context);

        result.ShouldBeFalse();
    }

    #endregion

    #region IsSatisfiedBy — Empty AvailableIn

    [Fact]
    public void IsSatisfiedBy_ShouldReturnOk_WhenAvailableInIsEmpty()
    {
        var context = CreateContextWithSharedTransition("cancel", "cancelled", "review");

        var result = _specification.IsSatisfiedBy(context);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_ShouldReturnOk_WhenAvailableInIsEmpty_FromAnyState()
    {
        var context = CreateContextWithSharedTransition("cancel", "cancelled", "some-random-state");

        var result = _specification.IsSatisfiedBy(context);

        result.IsSuccess.ShouldBeTrue();
    }

    #endregion

    #region IsSatisfiedBy — Populated AvailableIn

    [Fact]
    public void IsSatisfiedBy_ShouldReturnOk_WhenCurrentStateIsInAvailableIn()
    {
        var context = CreateContextWithSharedTransition(
            "cancel", "cancelled", "review",
            availableIn: ["review", "pending"]);

        var result = _specification.IsSatisfiedBy(context);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_ShouldReturnFail_WhenCurrentStateIsNotInAvailableIn()
    {
        var context = CreateContextWithSharedTransition(
            "cancel", "cancelled", "initial",
            availableIn: ["review", "pending"]);

        var result = _specification.IsSatisfiedBy(context);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.SharedTransitionNotAvailableInState);
    }

    #endregion

    #region IsSatisfiedBy — Error Boundary

    [Fact]
    public void IsSatisfiedBy_ShouldReturnOk_WhenIsErrorBoundaryTransition_EvenIfNotInAvailableIn()
    {
        var context = CreateContextWithSharedTransition(
            "cancel", "cancelled", "initial",
            availableIn: ["review"],
            isErrorBoundary: true);

        var result = _specification.IsSatisfiedBy(context);

        result.IsSuccess.ShouldBeTrue();
    }

    #endregion

    #region IsSatisfiedBy — Transition Not Found

    [Fact]
    public void IsSatisfiedBy_ShouldReturnFail_WhenTransitionIsNull()
    {
        var context = CreateContextWithNullTransition();

        var result = _specification.IsSatisfiedBy(context);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("TransitionNotFound");
    }

    #endregion

    #region Helpers

    private static TransitionExecutionContext CreateContextWithSharedTransition(
        string transitionKey,
        string target,
        string currentStateKey,
        string[]? availableIn = null,
        bool isErrorBoundary = false)
    {
        var workflow = Workflow.Create();
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        workflow.SetType("F");

        var currentState = State.Create(currentStateKey, StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(currentState);

        var targetState = State.Create(target, StateType.Finish, StateSubType.Cancelled, "Patch");
        workflow.AddState(targetState);

        var sharedTransition = Transition.Create(transitionKey, null, target, TriggerType.Manual, "Patch");

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

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-flow",
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Workflow = workflow,
            Current = currentState,
            Transition = sharedTransition,
            Instance = instance,
            IsErrorBoundaryTransition = isErrorBoundary
        };
    }

    private static TransitionExecutionContext CreateContextWithStateTransition(
        string transitionKey,
        string fromState,
        string toState)
    {
        var workflow = Workflow.Create();
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        workflow.SetType("F");

        var currentState = State.Create(fromState, StateType.Intermediate, StateSubType.None, "Patch");
        var stateTransition = Transition.Create(transitionKey, fromState, toState, TriggerType.Manual, "Patch");
        currentState.AddTransition(stateTransition);
        workflow.AddState(currentState);

        var targetState = State.Create(toState, StateType.Finish, StateSubType.Success, "Patch");
        workflow.AddState(targetState);

        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "sys_flows", "1.0.0", "test-key");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-flow",
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Workflow = workflow,
            Current = currentState,
            Transition = stateTransition,
            Instance = instance
        };
    }

    private static TransitionExecutionContext CreateContextWithNullTransition()
    {
        var workflow = Workflow.Create();
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        workflow.SetType("F");

        var sharedTransition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");
        workflow.AddSharedTransition(sharedTransition);

        var currentState = State.Create("review", StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(currentState);

        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "sys_flows", "1.0.0", "test-key");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-flow",
            TransitionKey = "cancel",
            Trigger = TriggerType.Manual,
            Workflow = workflow,
            Current = currentState,
            Transition = null,
            Instance = instance
        };
    }

    #endregion
}
