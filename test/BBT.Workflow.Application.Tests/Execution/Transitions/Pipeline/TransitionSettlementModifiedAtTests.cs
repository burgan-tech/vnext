using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// A same-state hop leaves its <c>Instances.ModifiedAt</c> stamp pending (<c>ChangeStateStep</c>) for the write
/// that closes it. <see cref="TransitionSettlement"/> is where that is known: the Busy release stamps the row
/// itself, so the aggregate is left alone; without it — parked in Busy, waiting on a SubFlow, or not the owner —
/// the settlement stamps the aggregate and the commit sends it. Exactly one of the two, never both.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class TransitionSettlementModifiedAtTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";
    private const string WorkflowVersion = "1.0.0";
    private static readonly DateTime LongAgo = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The start into the initial state resting Active: the release CAS stamps; no second write.</summary>
    [Fact]
    public async Task Pending_stamp_closed_by_the_Busy_release_is_not_stamped_again()
    {
        var context = CreateContext(ownsStatus: true, pending: true);
        var repository = Substitute.For<IInstanceRepository>();
        repository.TryReleaseBusyAsync(context.Instance, Arg.Any<CancellationToken>()).Returns(true);

        await Apply(context, InstanceStatus.Active, repository);

        await repository.Received(1).TryReleaseBusyAsync(context.Instance, Arg.Any<CancellationToken>());
        context.Instance.ModifiedAt.ShouldBe(LongAgo);
    }

    /// <summary>A same-state hop that ends parked in Busy (a Busy-subtype target): no release, so settlement stamps.</summary>
    [Fact]
    public async Task Pending_stamp_of_a_hop_that_stays_Busy_is_applied()
    {
        var parked = State.Create("waiting", StateType.Intermediate, StateSubType.Busy, VersionStrategy.IncreaseMinor.Code);
        var context = CreateContext(ownsStatus: true, pending: true, target: parked);
        var repository = Substitute.For<IInstanceRepository>();

        await Apply(context, InstanceStatus.Active, repository);

        await repository.DidNotReceive().TryReleaseBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
        context.Instance.ModifiedAt.ShouldNotBe(LongAgo);
    }

    /// <summary>
    /// The case that showed status ownership is the wrong signal: a parent waiting on its SubFlow re-entered by
    /// a job — classified as the owner, yet nothing resolves Active and no release is attempted. Settlement
    /// stamps it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pending_stamp_without_a_release_attempt_is_applied_whatever_the_ownership(bool ownsStatus)
    {
        var context = CreateContext(ownsStatus, pending: true);
        var repository = Substitute.For<IInstanceRepository>();

        await Apply(context, resolvedStatus: null, repository);

        await repository.DidNotReceive().TryReleaseBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
        context.Instance.ModifiedAt.ShouldNotBe(LongAgo);
    }

    /// <summary>
    /// The release is attempted but its CAS is lost (the row is no longer Busy): it wrote nothing, so it stamped
    /// nothing, and settlement stamps the aggregate. Attempting the release is not the same as closing the hop.
    /// </summary>
    [Fact]
    public async Task Pending_stamp_whose_release_CAS_is_lost_is_applied()
    {
        var context = CreateContext(ownsStatus: true, pending: true);
        var repository = Substitute.For<IInstanceRepository>();
        repository.TryReleaseBusyAsync(context.Instance, Arg.Any<CancellationToken>()).Returns(false);

        await Apply(context, InstanceStatus.Active, repository);

        await repository.Received(1).TryReleaseBusyAsync(context.Instance, Arg.Any<CancellationToken>());
        context.Instance.ModifiedAt.ShouldNotBe(LongAgo);
    }

    /// <summary>Nothing pending (a state change already stamped, updateData, a deferred Finish): settlement adds nothing.</summary>
    [Fact]
    public async Task Without_a_pending_stamp_settlement_leaves_ModifiedAt_alone()
    {
        var context = CreateContext(ownsStatus: false, pending: false);

        await Apply(context, resolvedStatus: null, Substitute.For<IInstanceRepository>());

        context.Instance.ModifiedAt.ShouldBe(LongAgo);
    }

    private static Task Apply(TransitionExecutionContext context, InstanceStatus? resolvedStatus, IInstanceRepository repository)
        => TransitionSettlement.ApplyAsync(
            context,
            resolvedStatus,
            scheduleNotification: false,
            repository,
            Substitute.For<IStateNotificationScheduler>(),
            NullLogger.Instance,
            CancellationToken.None,
            chainSettled: true);

    private static TransitionExecutionContext CreateContext(bool ownsStatus, bool pending, State? target = null)
    {
        var instance = Instance.Create(Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
        instance.Busy();
        instance.ModifiedAt = LongAgo;

        var state = target ?? State.Create("waiting", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreaseMinor.Code);
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", WorkflowVersion));
        workflow.AddState(state);

        var context = new TransitionExecutionContext
        {
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            InstanceId = instance.Id,
            TransitionKey = "refresh",
            Workflow = workflow,
            Instance = instance,
            Current = state,
            Target = state,
            OwnsStatus = ownsStatus
        };
        if (pending)
            context.Directives.DeferModifiedAtStamp();
        return context;
    }
}
