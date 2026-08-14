using System;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transitions.Context;

/// <summary>
/// Unit tests for <see cref="TransitionExecutionContextExtensions.IsSelfTargetTransition"/>.
/// A self-target transition changes no state, which is what lets the pipeline skip the state's
/// lifecycle steps — so misclassifying one either re-runs OnEntry for a state that was never
/// re-entered, or silently skips OnEntry for a state that genuinely was.
/// </summary>
public class SelfTargetTransitionTests
{
    private const string CurrentStateKey = "initial-contract";

    [Fact]
    public void IsSelfTargetTransition_WhenTargetIsSelfKeyword_ShouldBeTrue()
    {
        var context = CreateContext(WellKnownStateKeys.Self);

        context.IsSelfTargetTransition().ShouldBeTrue();
    }

    /// <summary>
    /// A literal target equal to the current state is deliberately NOT a self target. The same
    /// comparison holds for the start transition (instance pre-positioned into the initial state at
    /// creation) and for a retry after the state change already committed — in both, the state does
    /// need entering. Only the authored <c>$self</c> keyword declares "do not move".
    /// </summary>
    [Fact]
    public void IsSelfTargetTransition_WhenTargetIsLiteralCurrentState_ShouldBeFalse()
    {
        var context = CreateContext(CurrentStateKey);

        context.IsSelfTargetTransition().ShouldBeFalse();
    }

    [Fact]
    public void IsSelfTargetTransition_WhenTargetIsAnotherState_ShouldBeFalse()
    {
        var context = CreateContext("approved");

        context.IsSelfTargetTransition().ShouldBeFalse();
    }

    [Fact]
    public void IsSelfTargetTransition_WhenTransitionIsNull_ShouldBeFalse()
    {
        var context = CreateContext(target: null);

        context.IsSelfTargetTransition().ShouldBeFalse();
    }

    /// <summary>
    /// The timeout target comes from ApplyTimeoutStateStep, not from Transition.Target, so the
    /// transition's own target says nothing about whether the state changes.
    /// </summary>
    [Fact]
    public void IsSelfTargetTransition_WhenTimeoutTransition_ShouldBeFalse()
    {
        var context = CreateContext(WellKnownStateKeys.Self);
        context.Directives.MarkAsTimeoutTransition();

        context.IsSelfTargetTransition().ShouldBeFalse();
    }

    /// <summary>
    /// A subflow resume re-enters at ClearBusyOnResumeStep, which owns the target; ChangeStateStep
    /// is skipped entirely on that path.
    /// </summary>
    [Fact]
    public void IsSelfTargetTransition_WhenSubFlowResume_ShouldBeFalse()
    {
        var context = CreateContext(WellKnownStateKeys.Self);
        context.Directives.MarkAsSubFlowResume(Guid.NewGuid());

        context.IsSelfTargetTransition().ShouldBeFalse();
    }

    private static TransitionExecutionContext CreateContext(string? target)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        instance.ChangeState(State.Create(CurrentStateKey, StateType.Intermediate, StateSubType.None, "Patch"));

        return new TransitionExecutionContext
        {
            Domain = "test-domain",
            InstanceId = instance.Id,
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            Instance = instance,
            Transition = target is null
                ? null
                : Transition.Create("test-transition", CurrentStateKey, target, TriggerType.Manual, "Patch"),
        };
    }
}
