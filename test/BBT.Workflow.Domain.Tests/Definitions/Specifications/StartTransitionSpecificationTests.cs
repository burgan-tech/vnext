using System;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Unit tests for StartTransitionSpecification.
/// Tests validation that StartTransition can only be executed from Initial state.
/// </summary>
public class StartTransitionSpecificationTests
{
    private readonly StartTransitionSpecification _specification;

    public StartTransitionSpecificationTests()
    {
        _specification = new StartTransitionSpecification();
    }

    [Fact]
    public void Priority_ShouldBe70()
    {
        // Assert
        _specification.Priority.ShouldBe(70);
    }

    [Fact]
    public void IsApplicable_WhenTransitionKeyIsStartTransition_ShouldReturnTrue()
    {
        // Arrange
        var workflow = CreateMockWorkflow("start-transition");
        var context = CreateContext(workflow, "start-transition", StateType.Initial);

        // Act
        var result = _specification.IsApplicable(context);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void IsApplicable_WhenTransitionKeyIsNotStartTransition_ShouldReturnFalse()
    {
        // Arrange
        var workflow = CreateMockWorkflow("start-transition");
        var context = CreateContext(workflow, "other-transition", StateType.Initial);

        // Act
        var result = _specification.IsApplicable(context);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_WhenCurrentStateIsInitial_ShouldReturnSuccess()
    {
        // Arrange
        var workflow = CreateMockWorkflow("start-transition");
        var context = CreateContext(workflow, "start-transition", StateType.Initial);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_WhenCurrentStateIsIntermediate_ShouldReturnFailure()
    {
        // Arrange
        var workflow = CreateMockWorkflow("start-transition");
        var context = CreateContext(workflow, "start-transition", StateType.Intermediate);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Transition:100022");
        result.Error.Message.ShouldContain("Start transition can only be executed from Initial state");
        result.Error.Message.ShouldContain("Intermediate");
    }

    [Fact]
    public void IsSatisfiedBy_WhenCurrentStateIsFinish_ShouldReturnFailure()
    {
        // Arrange
        var workflow = CreateMockWorkflow("start-transition");
        var context = CreateContext(workflow, "start-transition", StateType.Finish);

        // Act
        var result = _specification.IsSatisfiedBy(context);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Transition:100022");
        result.Error.Message.ShouldContain("Initial state");
    }

    private static TransitionExecutionContext CreateContext(
        Workflow workflow,
        string transitionKey,
        StateType currentStateType)
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "sys_flows", "test-key");
        
        // Create current state with specified type
        var currentState = State.Create(
            $"state-{currentStateType}", 
            currentStateType, 
            StateSubType.Success, 
            "Patch");

        var context = new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Instance = instance,
            Workflow = workflow,
            Current = currentState
        };

        return context;
    }

    private static Workflow CreateMockWorkflow(string startTransitionKey)
    {
        // Use minimal workflow setup
        var workflow = Workflow.Create();
        
        // Create initial state
        var initialState = State.Create("initial", StateType.Initial, StateSubType.Success, "Patch");
        
        // Create start transition
        var startTransition = Transition.Create(
            startTransitionKey,
            "initial",
            "next-state",
            TriggerType.Manual,
            "Patch");

        workflow.AddState(initialState);
        workflow.SetStartTransition(startTransition);

        return workflow;
    }
}
