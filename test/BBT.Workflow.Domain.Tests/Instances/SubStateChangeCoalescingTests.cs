using System;
using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Events;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// <c>sub:state-changed</c> is coalesced to one event per activation episode.
/// <para>
/// <see cref="Instance.ChangeState"/> only ARMS the notification; the pipeline publishes it at the
/// episode's rest point via <see cref="Instance.PublishPendingSubStateChange"/>. An auto-chain
/// crossing A→B→C→D is one episode with one observable outcome — D — and the parent can act on
/// nothing in between, because the chain has not stopped. Publishing per hop made this the
/// runtime's highest-volume signal and moved the parent's state-function ETag on every hop.
/// </para>
/// <para>
/// The value the parent ends up with is unchanged: the LAST event of the old per-hop burst carried
/// the same <c>NewState</c> the single coalesced event carries now.
/// </para>
/// </summary>
public class SubStateChangeCoalescingTests : DomainTestBase<DomainEntryPoint>
{
    private static Instance CreateSubFlow()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubFlow.Code;
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Id] = Guid.NewGuid().ToString();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Domain] = "parent-domain";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Flow] = "parent-flow";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Version] = "1.0.0";
        instance.ClearDomainEvents();
        return instance;
    }

    private static State S(string key) => StateFactory.CreateDefault(key, StateType.Intermediate);

    private static InstanceSubStateChangedEvent[] SubStateEvents(Instance instance) =>
        instance.GetDomainEvents().Select(e => e.Event)
            .OfType<InstanceSubStateChangedEvent>()
            .ToArray();

    [Fact]
    public void ChangeState_AloneEmitsNothing()
    {
        var instance = CreateSubFlow();

        instance.ChangeState(S("b"));

        SubStateEvents(instance).ShouldBeEmpty();
    }

    /// <summary>
    /// The heart of it: three hops, one event, carrying the FINAL state — byte-for-byte the
    /// <c>NewState</c> the old burst's last event carried.
    /// </summary>
    [Fact]
    public void AnAutoChain_CollapsesToOneEventCarryingTheFinalState()
    {
        var instance = CreateSubFlow();
        var start = instance.GetCurrentState;

        instance.ChangeState(S("b"));
        instance.ChangeState(S("c"));
        instance.ChangeState(S("d"));
        instance.PublishPendingSubStateChange();

        var published = SubStateEvents(instance).ShouldHaveSingleItem();
        published.NewState.ShouldBe("d");
        published.PreviousState.ShouldBe(start);
    }

    [Fact]
    public void PublishingTwice_EmitsOnce()
    {
        var instance = CreateSubFlow();
        instance.ChangeState(S("b"));

        instance.PublishPendingSubStateChange();
        instance.PublishPendingSubStateChange();

        SubStateEvents(instance).Length.ShouldBe(1);
    }

    [Fact]
    public void ARestPointWithNoStateChange_EmitsNothing()
    {
        var instance = CreateSubFlow();

        instance.PublishPendingSubStateChange();

        SubStateEvents(instance).ShouldBeEmpty();
    }

    /// <summary>
    /// A <c>$self</c> transition is not a state change, and never was: the same-state guard in
    /// <c>ChangeState</c> predates the coalescing and still holds. Worth pinning, because a
    /// frequently invoked <c>$self</c> shared transition would otherwise notify the parent on every
    /// call for a state that never moved.
    /// </summary>
    [Fact]
    public void ASelfTransition_EmitsNothing()
    {
        var instance = CreateSubFlow();
        instance.ChangeState(S("b"));
        instance.PublishPendingSubStateChange();
        instance.ClearDomainEvents();

        instance.ChangeState(S("b"));
        instance.PublishPendingSubStateChange();

        SubStateEvents(instance).ShouldBeEmpty();
    }

    /// <summary>
    /// A chain that wanders and returns is, from the parent's point of view, a chain that did
    /// nothing — and nothing was published in between to correct.
    /// </summary>
    [Fact]
    public void AChainReturningToItsStartingState_EmitsNothing()
    {
        var instance = CreateSubFlow();
        instance.ChangeState(S("a"));
        instance.PublishPendingSubStateChange();
        instance.ClearDomainEvents();

        instance.ChangeState(S("b"));
        instance.ChangeState(S("a"));
        instance.PublishPendingSubStateChange();

        SubStateEvents(instance).ShouldBeEmpty();
    }

    [Fact]
    public void ARootInstance_NeverEmits()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ClearDomainEvents();

        instance.ChangeState(S("b"));
        instance.PublishPendingSubStateChange();

        SubStateEvents(instance).ShouldBeEmpty();
    }

    /// <summary>
    /// Each episode reports its own move. The second episode's <c>PreviousState</c> is where the
    /// first one left off, so the chain of events remains gap-free.
    /// </summary>
    [Fact]
    public void ASecondEpisode_ReportsItsOwnMove()
    {
        var instance = CreateSubFlow();
        instance.ChangeState(S("b"));
        instance.PublishPendingSubStateChange();
        instance.ClearDomainEvents();

        instance.ChangeState(S("c"));
        instance.PublishPendingSubStateChange();

        var published = SubStateEvents(instance).ShouldHaveSingleItem();
        published.PreviousState.ShouldBe("b");
        published.NewState.ShouldBe("c");
    }
}
