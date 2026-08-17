using System;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transitions.Context;

/// <summary>
/// Unit tests for <see cref="TransitionExecutionContextExtensions.IsSelfTargetTransition"/> and the
/// policy composed on top of it, <see cref="TransitionExecutionContextExtensions.SkipsStateLifecycle"/>.
/// <para>
/// Two separate claims, and conflating them has broken the pipeline in both directions. "The target
/// is <c>$self</c>" is a fact about the definition; "the state's lifecycle is skipped" is a policy
/// that applies to <c>updateData</c> alone. Get the first wrong and OnEntry is skipped for a state
/// that genuinely was entered (start, retry-after-commit); get the second wrong and every
/// <c>$self</c> shared transition loses its hooks and its timers get re-armed from zero.
/// </para>
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

    // ── SkipsStateLifecycle: the policy layered on top of the target check ───────

    /// <summary>
    /// <c>updateData</c> is the only transition that skips the state's lifecycle. Its target is
    /// fixed to <c>$self</c> by the validator, and writing data is all it does.
    /// </summary>
    [Fact]
    public void SkipsStateLifecycle_ForUpdateDataTargetingSelf_ShouldBeTrue()
    {
        var context = CreateContext(WellKnownStateKeys.Self, WellKnownTransitionKeys.UpdateData);

        context.SkipsStateLifecycle().ShouldBeTrue();
    }

    /// <summary>
    /// The case this predicate exists to separate: a shared transition declaring
    /// <c>target: $self</c> says "do not move the instance", NOT "skip the state's hooks". Reading
    /// the two as one instruction killed OnEntry and the timer re-arm for every such transition.
    /// </summary>
    [Theory]
    [InlineData("share-mark")]
    [InlineData("cancel")]
    [InlineData("exit")]
    public void SkipsStateLifecycle_ForANonUpdateDataTransitionTargetingSelf_ShouldBeFalse(
        string transitionKey)
    {
        var context = CreateContext(WellKnownStateKeys.Self, transitionKey);

        context.SkipsStateLifecycle().ShouldBeFalse();
    }

    /// <summary>Matches the workflow's CONFIGURED updateData key, not only the reserved alias.</summary>
    [Fact]
    public void SkipsStateLifecycle_ForTheConfiguredUpdateDataKey_ShouldBeTrue()
    {
        var context = CreateContext(WellKnownStateKeys.Self, "update-root-data");

        context.SkipsStateLifecycle().ShouldBeTrue();
    }

    [Fact]
    public void SkipsStateLifecycle_ForUpdateDataOnATimeoutExecution_ShouldBeFalse()
    {
        var context = CreateContext(WellKnownStateKeys.Self, WellKnownTransitionKeys.UpdateData);
        context.Directives.MarkAsTimeoutTransition();

        context.SkipsStateLifecycle().ShouldBeFalse();
    }

    [Fact]
    public void SkipsStateLifecycle_ForUpdateDataOnASubFlowResume_ShouldBeFalse()
    {
        var context = CreateContext(WellKnownStateKeys.Self, WellKnownTransitionKeys.UpdateData);
        context.Directives.MarkAsSubFlowResume(Guid.NewGuid());

        context.SkipsStateLifecycle().ShouldBeFalse();
    }

    private static TransitionExecutionContext CreateContext(
        string? target,
        string transitionKey = "test-transition")
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        instance.ChangeState(State.Create(CurrentStateKey, StateType.Intermediate, StateSubType.None, "Patch"));

        return new TransitionExecutionContext
        {
            Domain = "test-domain",
            InstanceId = instance.Id,
            WorkflowKey = "test-workflow",
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            // The updateData check falls through to Workflow.UpdateData when the key is not the
            // reserved alias, so a workflow is required for the negative cases too.
            Workflow = CreateWorkflow(),
            Instance = instance,
            Transition = target is null
                ? null
                : Transition.Create(transitionKey, CurrentStateKey, target, TriggerType.Manual, "Patch"),
        };
    }

    /// <summary>A workflow whose configured updateData key is <c>update-root-data</c>.</summary>
    private static Definitions.Workflow CreateWorkflow()
    {
        var json = """
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           { "key": "initial-contract", "stateType": "Intermediate", "transitions": [] }
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "updateData": {"key": "update-root-data", "from": null, "target": "$self", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null},
                       "startTransition": {"key": "start", "from": null, "target": "initial-contract", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        return System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
    }
}
