using System;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The effective projection's invariant: while a blocking SubFlow correlation is open the
/// <c>Effective*</c> quartet describes the LEAF, and the moment none is open it describes this
/// instance's OWN current state and status.
/// <para>
/// Before this was stated as an invariant it was false in three places, and each one was reachable.
/// <c>EffectiveStateSubType</c> had no reset writer at all, so a parent carried a finished child's
/// <c>Human</c> sub type forever. The state half of the reset was missing the
/// <c>!HasActiveSubFlow</c> guard the status half has, so with two open correlations the two halves
/// disagreed. And a level cancelled mid-subflow kept a live child's <c>A</c> in the raw status
/// column, because the cascade closes correlations after the level completes.
/// </para>
/// </summary>
public class InstanceEffectiveProjectionTests : DomainTestBase<DomainEntryPoint>
{
    private static State Human(string key) =>
        StateFactory.CreateDefault(key, StateType.Intermediate, StateSubType.Human);

    private static State Plain(string key) =>
        StateFactory.CreateDefault(key, StateType.Intermediate, StateSubType.None);

    private static State SubFlowState(string key) =>
        StateFactory.CreateDefault(key, StateType.SubFlow, StateSubType.None);

    private static Guid AddOpenSubFlow(Instance instance, string subFlowName = "kyc-check")
    {
        var subInstanceId = Guid.NewGuid();
        instance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            instance.GetCurrentState,
            subInstanceId,
            SubFlowType.SubFlow.Code,
            "compliance",
            subFlowName,
            "1.0.0"));
        return subInstanceId;
    }

    /// <summary>
    /// The leaf's projection arrives through the upward notification and must survive untouched;
    /// nothing about the parent's own state may leak into it while the child owns the view.
    /// </summary>
    [Fact]
    public void WhileASubFlowIsOpen_TheProjectionIsTheChilds()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ChangeState(SubFlowState("awaiting-sub"));
        AddOpenSubFlow(instance);

        instance.PropagateEffectiveStateToParent(
            "collect-documents", StateType.Intermediate, StateSubType.Human, InstanceStatus.Active);

        instance.GetEffectiveState.ShouldBe("collect-documents");
        instance.EffectiveStateSubType.ShouldBe(StateSubType.Human);
        instance.EffectiveStatus.ShouldBe(InstanceStatus.Active);

        // ...and the parent's own record of where IT is stays its own.
        instance.CurrentStateSubType.ShouldBe(StateSubType.None);
    }

    /// <summary>
    /// The defect this method exists for. <c>SetEffectiveState</c> moved only the state key, and
    /// <c>EffectiveStateSubType</c> has no other reset writer — the resume re-enters the pipeline
    /// at <c>ClearBusyOnResumeStep</c> (79), past <c>ChangeStateStep</c> (50), so a parent parked in
    /// a SubFlow state that waits for a human or an event never reaches another
    /// <see cref="Instance.ChangeState"/> to repair it. The stale <c>Human</c> sub type was listed
    /// as an open human task, for every caller.
    /// </summary>
    [Fact]
    public void WhenTheLastSubFlowCloses_TheWholeQuartetReturnsToTheInstancesOwnState()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ChangeState(SubFlowState("awaiting-sub"));
        var subInstanceId = AddOpenSubFlow(instance);

        instance.PropagateEffectiveStateToParent(
            "collect-documents", StateType.Intermediate, StateSubType.Human, InstanceStatus.Active);
        instance.EffectiveStateSubType.ShouldBe(StateSubType.Human);

        instance.CompleteCorrelation(subInstanceId, SubItemTerminalOutcome.Completed, DateTime.UtcNow);
        instance.ResyncEffectiveStateFromCurrent();
        instance.ResyncEffectiveStatus();

        instance.GetEffectiveState.ShouldBe("awaiting-sub");
        instance.EffectiveStateType.ShouldBe(StateType.SubFlow);
        instance.EffectiveStateSubType.ShouldBe(StateSubType.None);
        instance.EffectiveStatus.ShouldBe(instance.Status);
    }

    /// <summary>
    /// The guard, and why both halves need it. The status half has always had it; the state half
    /// did not, so one of two open correlations completing pulled the state back to the parent's
    /// own while the status still described the surviving child. Half a projection is worse than a
    /// stale one: no reader can tell which half to believe.
    /// </summary>
    [Fact]
    public void WithASecondSubFlowStillOpen_BothHalvesKeepDescribingTheSurvivingChild()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ChangeState(SubFlowState("awaiting-sub"));
        var first = AddOpenSubFlow(instance, "kyc-check");
        AddOpenSubFlow(instance, "credit-check");

        instance.PropagateEffectiveStateToParent(
            "collect-documents", StateType.Intermediate, StateSubType.Human, InstanceStatus.Active);

        instance.CompleteCorrelation(first, SubItemTerminalOutcome.Completed, DateTime.UtcNow);
        instance.ResyncEffectiveStateFromCurrent();
        instance.ResyncEffectiveStatus();

        instance.GetEffectiveState.ShouldBe("collect-documents");
        instance.EffectiveStateSubType.ShouldBe(StateSubType.Human);
        instance.EffectiveStatus.ShouldBe(InstanceStatus.Active);
    }

    /// <summary>
    /// With no correlation in play the quartet simply tracks <see cref="Instance.ChangeState"/>,
    /// which is what makes the human-task predicate readable off the parent row at all.
    /// </summary>
    [Fact]
    public void WithNoSubFlow_EveryStateChangeMovesTheProjectionWithIt()
    {
        var instance = InstanceFactory.CreateDefault();

        instance.ChangeState(Human("collect-documents"));

        instance.GetEffectiveState.ShouldBe("collect-documents");
        instance.EffectiveStateType.ShouldBe(StateType.Intermediate);
        instance.EffectiveStateSubType.ShouldBe(StateSubType.Human);

        instance.ChangeState(Plain("archived"));

        instance.GetEffectiveState.ShouldBe("archived");
        instance.EffectiveStateSubType.ShouldBe(StateSubType.None);
    }

    /// <summary>
    /// Cancelling a parent mid-subflow is the case the write side was documented as unable to
    /// repair: <see cref="Instance.ResyncEffectiveStatus"/> is a no-op while the correlation is
    /// open, and the cascade closes correlations afterwards. A terminal level can never legitimately
    /// project a live child, so the terminal transitions write their own values through.
    /// </summary>
    [Fact]
    public void CancellingMidSubFlow_RepairsTheRawProjectionInsteadOfLeavingTheChildsBehind()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ChangeState(SubFlowState("awaiting-sub"));
        AddOpenSubFlow(instance);

        instance.PropagateEffectiveStateToParent(
            "collect-documents", StateType.Intermediate, StateSubType.Human, InstanceStatus.Active);

        instance.Cancel("core");

        instance.EffectiveStatus.ShouldBe(instance.Status);
        instance.GetEffectiveState.ShouldBe("awaiting-sub");
        instance.EffectiveStateSubType.ShouldBe(StateSubType.None);
    }

    [Fact]
    public void CompletingMidSubFlow_RepairsTheRawProjectionToo()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ChangeState(SubFlowState("awaiting-sub"));
        AddOpenSubFlow(instance);

        instance.PropagateEffectiveStateToParent(
            "collect-documents", StateType.Intermediate, StateSubType.Human, InstanceStatus.Active);

        instance.Complete("core");

        instance.EffectiveStatus.ShouldBe(InstanceStatus.Completed);
        instance.EffectiveStateSubType.ShouldBe(StateSubType.None);
    }

    /// <summary>
    /// The served value was already right in both regimes — <see cref="Instance.GetEffectiveStatus"/>
    /// clamps a terminal own status through. Repairing the raw column changes what can be FILTERED
    /// and SORTED on, not what any reader is handed, and this pins that distinction so the repair
    /// cannot quietly become a contract change.
    /// </summary>
    [Fact]
    public void TheRepairChangesTheRawColumnAndNotTheServedValue()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ChangeState(SubFlowState("awaiting-sub"));
        AddOpenSubFlow(instance);
        instance.PropagateEffectiveStateToParent(
            "collect-documents", StateType.Intermediate, StateSubType.Human, InstanceStatus.Active);

        var servedBefore = instance.GetEffectiveStatus;
        instance.Cancel("core");

        instance.GetEffectiveStatus.ShouldBe(instance.Status);
        servedBefore.ShouldBe(InstanceStatus.Active);
    }

    /// <summary>
    /// <see cref="Instance.Fault"/> is deliberately NOT repaired: <see cref="Instance.Unfault"/>
    /// brings the level back to Active while its child may still be running, and nothing could
    /// restore the child's projection afterwards — a correlation carries a state key and a terminal
    /// outcome, never a live status. Faulted levels keep relying on the clamp.
    /// </summary>
    [Fact]
    public void Faulting_LeavesTheChildsProjectionForAPossibleRetry()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ChangeState(SubFlowState("awaiting-sub"));
        AddOpenSubFlow(instance);
        instance.PropagateEffectiveStateToParent(
            "collect-documents", StateType.Intermediate, StateSubType.Human, InstanceStatus.Active);

        instance.Fault("core");

        instance.EffectiveStatus.ShouldBe(InstanceStatus.Active);
        instance.EffectiveStateSubType.ShouldBe(StateSubType.Human);

        // The clamp is what keeps a reader honest here, and it still does.
        instance.GetEffectiveStatus.ShouldBe(InstanceStatus.Faulted);
    }
}
