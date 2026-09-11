using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// Pins <see cref="SubflowStateService"/>: the per-sub-item lock it now shares with the three
/// terminal paths, the narrow load, the monotonic ordering guard, and the second post-commit relay
/// call site that carries the fast path up to the grandparent when this parent is itself a subflow.
/// </summary>
public sealed class SubflowStateServiceTests
{
    private const string Domain = "bank";
    private const string ParentFlow = "parent-flow";

    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IInstanceRepository> _instanceRepository = new();
    private readonly Mock<ITransitionLockScopeFactory> _lockScopeFactory = new();
    private readonly Mock<ITransitionLockScope> _lockScope = new();
    private readonly Mock<IPostCommitRelayDispatcher> _relayDispatcher = new();
    private readonly Mock<ILogger<SubflowStateService>> _logger = new();

    public SubflowStateServiceTests()
    {
        _uow.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _uow.Setup(u => u.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _uowManager.Setup(m => m.Begin(It.IsAny<UnitOfWorkOptions>())).Returns(_uow.Object);

        _lockScope.SetupGet(x => x.IsAcquired).Returns(true);
        _lockScope.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _lockScopeFactory
            .Setup(x => x.AcquireAsync(
                It.IsAny<string>(), It.IsAny<LockAcquireWait>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_lockScope.Object);
    }

    private SubflowStateService CreateSut() => new(
        _uowManager.Object,
        _instanceRepository.Object,
        _lockScopeFactory.Object,
        _relayDispatcher.Object,
        Options.Create(new WorkflowExecutionOptions()),
        _logger.Object);

    /// <summary>
    /// A parent waiting on one child. <paramref name="asSubflow"/> makes the parent itself a subflow,
    /// which is what makes its own state write raise the grandparent's event.
    /// </summary>
    private static Instance CreateParent(out Guid subInstanceId, bool asSubflow = false)
    {
        subInstanceId = Guid.NewGuid();
        var parent = Instance.Create(Guid.NewGuid(), ParentFlow, "1.0.0", "parent-key");
        parent.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        parent.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            parent.Id,
            "waiting-child",
            subInstanceId,
            SubFlowType.SubFlow.Code,
            Domain,
            "child-flow",
            "1.0.0"));

        if (asSubflow)
        {
            parent.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubFlow.Code;
            parent.ExtraProperties[DomainConsts.MetaDataKeys.Id] = Guid.NewGuid().ToString();
            parent.ExtraProperties[DomainConsts.MetaDataKeys.Domain] = Domain;
            parent.ExtraProperties[DomainConsts.MetaDataKeys.Flow] = "grandparent-flow";
        }

        // Setup noise (correlation add, state change) is not what these tests measure.
        parent.ClearDomainEvents();
        return parent;
    }

    private void Loads(Instance parent, Guid subInstanceId) =>
        _instanceRepository
            .Setup(x => x.FindForSubflowStateChangeAsync(parent.Id, subInstanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

    private static SubFlowStateChangedInput Input(Guid parentId, Guid subInstanceId, DateTime changedAt) => new()
    {
        ParentInstanceId = parentId,
        SubInstanceId = subInstanceId,
        Domain = Domain,
        Flow = ParentFlow,
        Version = "1.0.0",
        NewState = "child-running",
        PreviousState = "child-start",
        NewStateType = StateType.Intermediate,
        NewStateSubType = StateSubType.None,
        ChangedAt = changedAt
    };

    [Fact]
    public async Task Applies_The_Change_And_Persists_The_Parent()
    {
        var parent = CreateParent(out var subInstanceId);
        Loads(parent, subInstanceId);
        var changedAt = DateTime.UtcNow;

        await CreateSut().UpdateParentStateAsync(Input(parent.Id, subInstanceId, changedAt));

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.SubFlowCurrentState.ShouldBe("child-running");
        correlation.SubFlowStateChangedAt.ShouldBe(changedAt);
        parent.GetEffectiveState.ShouldBe("child-running");
        _instanceRepository.Verify(
            x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The lock key must be byte-for-byte the one <c>SubflowCompletionService</c> /
    /// <c>SubflowFaultService</c> / <c>SubflowCancellationService</c> take, or a state change and a
    /// terminal outcome for the same child stop excluding each other.
    /// </summary>
    [Fact]
    public async Task Takes_The_Same_Per_SubItem_Lock_Key_The_Terminal_Paths_Take()
    {
        var parent = CreateParent(out var subInstanceId);
        Loads(parent, subInstanceId);

        await CreateSut().UpdateParentStateAsync(Input(parent.Id, subInstanceId, DateTime.UtcNow));

        var expected = $"vnext:{Domain}:{ParentFlow}:{parent.Id}:sub:{subInstanceId:N}";
        _lockScopeFactory.Verify(
            x => x.AcquireAsync(expected, It.IsAny<LockAcquireWait>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Throws_And_Loads_Nothing_When_The_Lock_Is_Not_Acquired()
    {
        var parent = CreateParent(out var subInstanceId);
        Loads(parent, subInstanceId);
        _lockScope.SetupGet(x => x.IsAcquired).Returns(false);

        await Should.ThrowAsync<SubflowTerminalLockNotAcquiredException>(
            () => CreateSut().UpdateParentStateAsync(Input(parent.Id, subInstanceId, DateTime.UtcNow)));

        _instanceRepository.Verify(
            x => x.FindForSubflowStateChangeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The narrow loader exists because the default detail load pulls the whole instance-data
    /// history, unsplit, on the runtime's highest-volume subflow signal.
    /// </summary>
    [Fact]
    public async Task Uses_The_Narrow_Loader_And_Never_The_Full_Detail_Load()
    {
        var parent = CreateParent(out var subInstanceId);
        Loads(parent, subInstanceId);

        await CreateSut().UpdateParentStateAsync(Input(parent.Id, subInstanceId, DateTime.UtcNow));

        _instanceRepository.Verify(
            x => x.FindForSubflowStateChangeAsync(parent.Id, subInstanceId, It.IsAny<CancellationToken>()),
            Times.Once);
        _instanceRepository.Verify(
            x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Rejects_A_Stale_Event_Without_Persisting_Or_Relaying()
    {
        var parent = CreateParent(out var subInstanceId, asSubflow: true);
        var applied = DateTime.UtcNow;
        parent.FindCorrelationBySubInstanceId(subInstanceId)!.UpdateSubFlowState("newer", applied);
        Loads(parent, subInstanceId);

        await CreateSut().UpdateParentStateAsync(
            Input(parent.Id, subInstanceId, applied.AddMilliseconds(-5)));

        parent.FindCorrelationBySubInstanceId(subInstanceId)!.SubFlowCurrentState.ShouldBe("newer");
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _relayDispatcher.Verify(
            x => x.RelayAsync(It.IsAny<IReadOnlyList<DomainEventEnvelope>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An equal timestamp is a duplicate delivery of the SAME event, not a reorder. Re-applying it is
    /// idempotent, and rejecting it would close the only recovery path a redelivery has — so the
    /// guard stays strictly-older, exactly as it was before the lock was introduced.
    /// </summary>
    [Fact]
    public async Task Accepts_A_Redelivery_Carrying_The_Same_Timestamp()
    {
        var parent = CreateParent(out var subInstanceId);
        var changedAt = DateTime.UtcNow;
        parent.FindCorrelationBySubInstanceId(subInstanceId)!.UpdateSubFlowState("child-running", changedAt);
        Loads(parent, subInstanceId);

        await CreateSut().UpdateParentStateAsync(Input(parent.Id, subInstanceId, changedAt));

        _instanceRepository.Verify(
            x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Skips_Everything_When_The_Correlation_Is_Already_Closed()
    {
        var parent = Instance.Create(Guid.NewGuid(), ParentFlow, "1.0.0", "parent-key");
        var subInstanceId = Guid.NewGuid();
        _instanceRepository
            .Setup(x => x.FindForSubflowStateChangeAsync(parent.Id, subInstanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        await CreateSut().UpdateParentStateAsync(Input(parent.Id, subInstanceId, DateTime.UtcNow));

        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _relayDispatcher.Verify(
            x => x.RelayAsync(It.IsAny<IReadOnlyList<DomainEventEnvelope>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The depth ≥ 2 call site: when the parent is itself a subflow, its own write raises the
    /// grandparent's event, and those events go to the dispatcher after the commit so the fast path
    /// continues up the chain instead of dropping to broker latency one level up.
    /// </summary>
    [Fact]
    public async Task Relays_The_Grandparent_Event_When_The_Parent_Is_Itself_A_Subflow()
    {
        var parent = CreateParent(out var subInstanceId, asSubflow: true);
        Loads(parent, subInstanceId);

        IReadOnlyList<DomainEventEnvelope>? relayed = null;
        _relayDispatcher
            .Setup(x => x.RelayAsync(It.IsAny<IReadOnlyList<DomainEventEnvelope>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<DomainEventEnvelope> e, CancellationToken _) => relayed = e)
            .Returns(Task.CompletedTask);

        await CreateSut().UpdateParentStateAsync(Input(parent.Id, subInstanceId, DateTime.UtcNow));

        relayed.ShouldNotBeNull();
        relayed!.Select(e => e.Event).OfType<InstanceSubStateChangedEvent>()
            .ShouldContain(e => e.SubInstanceId == parent.Id && e.NewState == "child-running");
    }

    [Fact]
    public async Task Relays_Nothing_When_The_Parent_Is_Not_Itself_A_Subflow()
    {
        var parent = CreateParent(out var subInstanceId);
        Loads(parent, subInstanceId);

        IReadOnlyList<DomainEventEnvelope>? relayed = null;
        _relayDispatcher
            .Setup(x => x.RelayAsync(It.IsAny<IReadOnlyList<DomainEventEnvelope>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<DomainEventEnvelope> e, CancellationToken _) => relayed = e)
            .Returns(Task.CompletedTask);

        await CreateSut().UpdateParentStateAsync(Input(parent.Id, subInstanceId, DateTime.UtcNow));

        relayed.ShouldNotBeNull();
        relayed!.ShouldBeEmpty();
    }

    // ─── ordering by sequence, and the status the notification carries ───────

    private static SubFlowStateChangedInput Input(
        Guid parentId,
        Guid subInstanceId,
        DateTime changedAt,
        string? newStatus,
        long notificationSeq,
        string newState = "child-running") =>
        Input(parentId, subInstanceId, changedAt) with
        {
            NewState = newState,
            NewStatus = newStatus,
            NotificationSeq = notificationSeq
        };

    /// <summary>
    /// The sequence replaces the wall clock as the ordering authority. Here the stale delivery
    /// carries a NEWER timestamp and a LOWER sequence — exactly the shape two pods with skewed
    /// clocks produce — and it must still be rejected.
    /// </summary>
    [Fact]
    public async Task Rejects_A_Lower_Sequence_Even_When_Its_Timestamp_Looks_Newer()
    {
        var parent = CreateParent(out var subInstanceId);
        var applied = DateTime.UtcNow;
        parent.FindCorrelationBySubInstanceId(subInstanceId)!.UpdateSubFlowState("newer", applied, 7);
        Loads(parent, subInstanceId);

        await CreateSut().UpdateParentStateAsync(
            Input(parent.Id, subInstanceId, applied.AddSeconds(5), InstanceStatus.Active.Code, 6));

        parent.FindCorrelationBySubInstanceId(subInstanceId)!.SubFlowCurrentState.ShouldBe("newer");
        _instanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The mirror of the case above and the reason the sequence exists: a legitimate notification
    /// whose clock runs behind the one already applied. Under the timestamp guard it was discarded,
    /// and when it was the one taking the ancestors OUT of Busy nothing later corrected it.
    /// </summary>
    [Fact]
    public async Task Accepts_A_Higher_Sequence_Even_When_Its_Timestamp_Looks_Older()
    {
        var parent = CreateParent(out var subInstanceId);
        var applied = DateTime.UtcNow;
        parent.FindCorrelationBySubInstanceId(subInstanceId)!.UpdateSubFlowState("older", applied, 3);
        Loads(parent, subInstanceId);

        await CreateSut().UpdateParentStateAsync(
            Input(parent.Id, subInstanceId, applied.AddSeconds(-5), InstanceStatus.Active.Code, 4));

        var correlation = parent.FindCorrelationBySubInstanceId(subInstanceId)!;
        correlation.SubFlowCurrentState.ShouldBe("child-running");
        correlation.SubFlowNotificationSeq.ShouldBe(4);
        _instanceRepository.Verify(
            x => x.UpdateAsync(parent, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Applies_The_Reported_Status_To_The_Parents_Projection()
    {
        var parent = CreateParent(out var subInstanceId);
        Loads(parent, subInstanceId);

        await CreateSut().UpdateParentStateAsync(
            Input(parent.Id, subInstanceId, DateTime.UtcNow, InstanceStatus.Active.Code, 1));

        parent.EffectiveStatus.ShouldBe(InstanceStatus.Active);
    }

    /// <summary>
    /// A notification with no status — an older publisher — must leave the projection alone rather
    /// than guess one.
    /// </summary>
    [Fact]
    public async Task Leaves_The_Projection_Alone_When_No_Status_Was_Reported()
    {
        var parent = CreateParent(out var subInstanceId);
        parent.SetEffectiveStatus(InstanceStatus.Busy);
        Loads(parent, subInstanceId);

        await CreateSut().UpdateParentStateAsync(
            Input(parent.Id, subInstanceId, DateTime.UtcNow, newStatus: null, notificationSeq: 1));

        parent.EffectiveStatus.ShouldBe(InstanceStatus.Busy);
    }

    /// <summary>
    /// The upward half of the release: when only the STATUS moved, the parent must still raise its
    /// own notification, or the walk stops one level below the instance the client is polling.
    /// </summary>
    [Fact]
    public async Task Raises_The_Grandparent_Notification_For_A_Status_Only_Change()
    {
        var parent = CreateParent(out var subInstanceId, asSubflow: true);
        var applied = DateTime.UtcNow;
        parent.FindCorrelationBySubInstanceId(subInstanceId)!.UpdateSubFlowState("child-running", applied, 1);
        parent.SetEffectiveState("child-running");
        parent.SetEffectiveStatus(InstanceStatus.Busy);
        parent.ClearDomainEvents();
        Loads(parent, subInstanceId);

        await CreateSut().UpdateParentStateAsync(
            Input(parent.Id, subInstanceId, applied, InstanceStatus.Active.Code, 2));

        parent.EffectiveStatus.ShouldBe(InstanceStatus.Active);
        _relayDispatcher.Verify(
            x => x.RelayAsync(
                It.Is<IReadOnlyList<DomainEventEnvelope>>(e => e.Count > 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
