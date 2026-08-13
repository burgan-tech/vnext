using System;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Unit tests for <see cref="WellKnownTransitionSpecification"/>.
/// The well-known transitions (cancel / updateData / exit) bypass the state's transition list, but they
/// do honour their own <c>availableIn</c> restriction — before this gate existed they could be executed
/// from any state regardless of what the definition declared.
/// </summary>
public class WellKnownTransitionSpecificationTests
{
    private const string ReviewState = "review";
    private const string PendingState = "pending";

    private readonly WellKnownTransitionSpecification _specification = new();

    [Fact]
    public void Priority_ShouldBe30()
    {
        _specification.Priority.ShouldBe(30);
    }

    #region IsApplicable

    [Theory]
    [InlineData("cancel")]
    [InlineData("update-parent-data")]
    [InlineData("exit")]
    public void IsApplicable_ShouldReturnTrue_ForReservedAliases(string key)
    {
        var context = CreateContext(key, exitKey: "exit");

        _specification.IsApplicable(context).ShouldBeTrue();
    }

    [Fact]
    public void IsApplicable_ShouldReturnTrue_ForConfiguredCustomKey()
    {
        // A workflow may name its exit transition anything. Matching only the reserved aliases used to
        // leave a custom-keyed well-known transition unclaimed by every specification.
        var context = CreateContext("leave-process", exitKey: "leave-process");

        _specification.IsApplicable(context).ShouldBeTrue();
    }

    [Fact]
    public void IsApplicable_ShouldReturnFalse_ForUnrelatedKey()
    {
        var context = CreateContext("submit", exitKey: "exit");

        _specification.IsApplicable(context).ShouldBeFalse();
    }

    #endregion

    #region IsSatisfiedBy

    [Fact]
    public void IsSatisfiedBy_ShouldSucceed_WhenAvailableInIsEmpty()
    {
        var context = CreateContext("exit", exitKey: "exit", currentStateKey: PendingState);

        _specification.IsSatisfiedBy(context).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_ShouldSucceed_WhenCurrentStateIsListed()
    {
        var context = CreateContext("exit", exitKey: "exit",
            currentStateKey: ReviewState, availableIn: [ReviewState]);

        _specification.IsSatisfiedBy(context).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_ShouldFail_WhenCurrentStateIsNotListed()
    {
        var context = CreateContext("exit", exitKey: "exit",
            currentStateKey: PendingState, availableIn: [ReviewState]);

        var result = _specification.IsSatisfiedBy(context);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.WellKnownTransitionNotAvailableInState);
    }

    [Fact]
    public void IsSatisfiedBy_ShouldFail_ForCustomKeyedTransitionOutsideAvailableIn()
    {
        var context = CreateContext("leave-process", exitKey: "leave-process",
            currentStateKey: PendingState, availableIn: [ReviewState]);

        _specification.IsSatisfiedBy(context).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_ShouldSucceed_WhenErrorBoundaryTransition()
    {
        // Error-boundary transitions are allowed from any state, matching
        // SharedTransitionAvailabilitySpecification.
        var context = CreateContext("exit", exitKey: "exit",
            currentStateKey: PendingState, availableIn: [ReviewState], isErrorBoundary: true);

        _specification.IsSatisfiedBy(context).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_ShouldSucceed_WhenTransitionNotConfigured()
    {
        // Reserved alias requested but the workflow does not configure it: existence is reported by
        // Workflow.ResolveWellKnownKey during resolution, so this specification adds nothing.
        var context = CreateContext("cancel", exitKey: "exit");

        _specification.IsSatisfiedBy(context).IsSuccess.ShouldBeTrue();
    }

    #endregion

    #region Helpers

    private static TransitionExecutionContext CreateContext(
        string requestedKey,
        string exitKey,
        string currentStateKey = ReviewState,
        string[]? availableIn = null,
        bool isErrorBoundary = false)
    {
        var workflow = Workflow.Create();
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        workflow.SetType("F");

        var currentState = State.Create(currentStateKey, StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(currentState);
        if (currentStateKey != ReviewState)
            workflow.AddState(State.Create(ReviewState, StateType.Intermediate, StateSubType.None, "Patch"));

        var exited = State.Create("exited", StateType.Finish, StateSubType.Success, "Patch");
        workflow.AddState(exited);

        var exit = Transition.Create(exitKey, null, "exited", TriggerType.Manual, "Patch");
        foreach (var state in availableIn ?? [])
            exit.AddAvailableIn(state);
        workflow.SetExit(exit);

        var instanceId = Guid.NewGuid();

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-flow",
            TransitionKey = requestedKey,
            Trigger = TriggerType.Manual,
            Workflow = workflow,
            Current = currentState,
            Transition = exit,
            Instance = Instance.Create(instanceId, "sys_flows", "1.0.0", "test-key"),
            IsErrorBoundaryTransition = isErrorBoundary
        };
    }

    #endregion
}
