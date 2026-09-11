using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

public sealed class InstanceBusyManagerTests
{
    private readonly Mock<IInstanceRepository> _instanceRepository = new();
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IInstanceCommandGateway> _instanceCommandGateway = new();

    public InstanceBusyManagerTests()
    {
        _uow.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _uow.Setup(u => u.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _uowManager
            .Setup(m => m.Begin(It.IsAny<UnitOfWorkOptions>()))
            .Returns(_uow.Object);
    }

    private InstanceBusyManager CreateSut() =>
        new(
            _instanceRepository.Object,
            _uowManager.Object,
            _instanceCommandGateway.Object,
            NullLogger<InstanceBusyManager>.Instance);

    // ─── MarkBusyAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task MarkBusyAsync_WhenInstanceNotFound_ShouldSkipUoW()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        _instanceRepository
            .Setup(r => r.GetResultAsync(instanceId.ToString(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Instance>.Fail(Error.NotFound("instance-not-found", "Instance not found")));

        // Act
        await CreateSut().MarkBusyAsync(instanceId);

        // Assert — the authoritative read happens inside the isolated UoW, but no write occurs
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Once);
        _instanceRepository.Verify(r => r.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkBusyAsync_WhenInstanceAlreadyBusy_ShouldSkipUoW()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0");
        instance.Busy(); // mark busy first

        _instanceRepository
            .Setup(r => r.GetResultAsync(instanceId.ToString(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok(instance));

        // Act
        await CreateSut().MarkBusyAsync(instanceId);

        // Assert
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Once);
        _instanceRepository.Verify(r => r.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkBusyAsync_WhenInstanceActive_ShouldMarkBusyAndCommit()
    {
        // Arrange
        var instanceId = Guid.NewGuid();

        // The manager is a compare-and-set now: no aggregate load, the guard lives in the
        // repository's WHERE clause and the flag is the authoritative outcome.
        _instanceRepository
            .Setup(r => r.TryMarkBusyAsync(instanceId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()))
            .ReturnsAsync(true);

        // Act
        var flipped = await CreateSut().MarkBusyAsync(instanceId);

        // Assert — UoW opened with RequiresNew, CAS + commit called
        flipped.ShouldBeTrue();
        _uowManager.Verify(m => m.Begin(It.Is<UnitOfWorkOptions>(o =>
            o.Scope == UnitOfWorkScopeOption.RequiresNew)), Times.Once);
        _instanceRepository.Verify(r => r.TryMarkBusyAsync(instanceId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── MarkBusyWithPropagationAsync ────────────────────────────────────────

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenInstanceNotFound_ShouldNoOp()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance?)null);

        // Act
        await CreateSut().MarkBusyWithPropagationAsync(instanceId);

        // Assert
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Once);
        _instanceCommandGateway.Verify(g => g.MarkBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenNoSubflow_ShouldMarkButNotCallGateway()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0");
        // No subflow correlation added — instance.Subflow will be null

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        _instanceRepository
            .Setup(r => r.TryMarkBusyAsync(instanceId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()))
            .ReturnsAsync(true);

        // Act
        await CreateSut().MarkBusyWithPropagationAsync(instanceId);

        // Assert — UoW opened, CAS write, gateway NOT called
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Once);
        _instanceRepository.Verify(r => r.TryMarkBusyAsync(instanceId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _instanceCommandGateway.Verify(g => g.MarkBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenSubflowActive_ShouldPropagateToGateway()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var subflowInstanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0");

        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instanceId,
            "state-waiting",
            subflowInstanceId,
            "S", // SubFlow type
            "test-domain",
            "sub-flow",
            "1.0.0");

        instance.AddCorrelation(correlation);
        // AddCorrelation calls Busy() internally for SubFlow type — reset for a cleaner test
        // by providing a fresh instance and correlation
        var freshInstance = Instance.Create(instanceId, "test-flow", "1.0.0");
        var freshCorrelation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instanceId,
            "state-waiting",
            subflowInstanceId,
            "S",
            "sub-domain",
            "sub-flow",
            "2.0.0");
        // Use a fresh instance without AddCorrelation to control IsBusy state
        // Instead, mock FindWithActiveSubFlowAsync to return a pre-configured instance
        var activeInstance = Instance.Create(instanceId, "test-flow", "1.0.0");
        activeInstance.AddCorrelation(freshCorrelation);
        // AddCorrelation marks Busy — call Active() if available, or we test that gateway is called
        // regardless. The instance is already Busy after AddCorrelation, so MarkBusy is skipped,
        // but gateway call still propagates.

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeInstance);

        _instanceCommandGateway
            .Setup(g => g.MarkBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BBT.Aether.Results.Result<BBT.Workflow.Gateway.MarkBusyOutput>.Ok(
                new BBT.Workflow.Gateway.MarkBusyOutput { EffectiveStatusCode = InstanceStatus.Busy.Code }));

        // Act
        await CreateSut().MarkBusyWithPropagationAsync(instanceId);

        // Assert — gateway called with the subflow's InstanceId
        _instanceCommandGateway.Verify(g => g.MarkBusyAsync(
            It.Is<MarkBusyInput>(i => i.InstanceId == subflowInstanceId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenInstanceCompleted_ShouldNotPropagate()
    {
        // A terminal parent's correlation is being closed, not extended — Busy must not be
        // propagated to a subflow of an instance that is already done.
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0");
        instance.Complete("test-domain");

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        await CreateSut().MarkBusyWithPropagationAsync(instanceId);

        _instanceRepository.Verify(r => r.TryMarkBusyAsync(instanceId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()), Times.Never);
        _instanceCommandGateway.Verify(g => g.MarkBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── TryMarkBusyWithPropagationAsync ─────────────────────────────────────

    [Fact]
    public async Task TryMarkBusyWithPropagationAsync_WhenInstanceNotFound_ShouldReturnSkipped()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance?)null);

        // Act
        var outcome = await CreateSut().TryMarkBusyWithPropagationAsync(instanceId);

        // Assert
        outcome.ShouldBe(BusyMarkOutcome.Skipped);
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Once);
        _instanceCommandGateway.Verify(g => g.MarkBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryMarkBusyWithPropagationAsync_WhenAlreadyBusy_ShouldReturnAlreadyBusyWithoutPropagation()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0");
        instance.Busy();

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        // Act
        var outcome = await CreateSut().TryMarkBusyWithPropagationAsync(instanceId);

        // Assert — the second check runs inside the UoW and short-circuits without a write
        outcome.ShouldBe(BusyMarkOutcome.AlreadyBusy);
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Once);
        _instanceCommandGateway.Verify(g => g.MarkBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryMarkBusyWithPropagationAsync_WhenInstanceActive_ShouldMarkAndReturnMarked()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0");

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        _instanceRepository
            .Setup(r => r.TryMarkBusyAsync(instanceId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()))
            .ReturnsAsync(true);

        // Act
        var outcome = await CreateSut().TryMarkBusyWithPropagationAsync(instanceId);

        // Assert
        outcome.ShouldBe(BusyMarkOutcome.Marked);
        _uowManager.Verify(m => m.Begin(It.Is<UnitOfWorkOptions>(o =>
            o.Scope == UnitOfWorkScopeOption.RequiresNew)), Times.Once);
        _instanceRepository.Verify(r => r.TryMarkBusyAsync(instanceId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── MarkBusyWithPropagationAsync / ReleaseWithPropagationAsync (chain reserve) ──────────

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenAlreadyBusyParent_ShouldStillPropagateToSubflow()
    {
        // A parent holding an open SubFlow correlation is Busy for that subflow's whole lifetime,
        // so the accept-time chain reserve MUST look past it — the leaf is the only level a
        // long-polling client observes. (Contrast the Try- variant, which short-circuits.)
        var parentId = Guid.NewGuid();
        var subInstanceId = Guid.NewGuid();
        var parent = CreateParentWithActiveSubflow(parentId, subInstanceId);

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(parentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        await CreateSut().MarkBusyWithPropagationAsync(parentId);

        parent.IsBusy.ShouldBeTrue();
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Once); // read-only re-check
        _instanceCommandGateway.Verify(g => g.MarkBusyAsync(
            It.Is<MarkBusyInput>(i => i.InstanceId == subInstanceId && i.Workflow == "child-flow"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseWithPropagationAsync_WhenInstanceHoldsActiveSubflow_ShouldRecurseWithoutReleasingIt()
    {
        // The parent's Busy was never taken by the chain reserve — releasing it here would settle
        // an instance that is legitimately mid-subflow.
        var parentId = Guid.NewGuid();
        var subInstanceId = Guid.NewGuid();
        var parent = CreateParentWithActiveSubflow(parentId, subInstanceId);

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(parentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        await CreateSut().ReleaseWithPropagationAsync(parentId);

        parent.IsBusy.ShouldBeTrue();
        _instanceRepository.Verify(r => r.GetResultAsync(
            parentId.ToString(), false, It.IsAny<CancellationToken>()), Times.Never);
        _instanceCommandGateway.Verify(g => g.ReleaseBusyAsync(
            It.Is<MarkBusyInput>(i => i.InstanceId == subInstanceId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseWithPropagationAsync_WhenLeafBusy_ShouldSettleItToActive()
    {
        var leafId = Guid.NewGuid();
        var leaf = Instance.Create(leafId, "child-flow", "1.0.0");
        leaf.Busy();

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(leafId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(leaf);
        _instanceRepository
            .Setup(r => r.TryReleaseBusyAsync(leafId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()))
            .ReturnsAsync(true);

        await CreateSut().ReleaseWithPropagationAsync(leafId);

        // The settle is a set-based CAS in the repository; the in-memory copy is untouched.
        _instanceRepository.Verify(r => r.TryReleaseBusyAsync(leafId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _instanceCommandGateway.Verify(g => g.ReleaseBusyAsync(
            It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReleaseWithPropagationAsync_WhenInstanceNotFound_ShouldNoOp()
    {
        var instanceId = Guid.NewGuid();
        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance?)null);

        await CreateSut().ReleaseWithPropagationAsync(instanceId);

        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Never);
        _instanceCommandGateway.Verify(g => g.ReleaseBusyAsync(
            It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Instance CreateParentWithActiveSubflow(Guid parentId, Guid subInstanceId)
    {
        var parent = Instance.Create(parentId, "parent-flow", "1.0.0", "parent-key");
        parent.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        // AddCorrelation flips the parent Busy for the subflow's lifetime.
        parent.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), parentId, "waiting-child", subInstanceId,
            SubFlowType.SubFlow.Code, "bank", "child-flow", "1.0.0"));
        return parent;
    }

    // ─── EffectiveStatus propagation (Edge A) ────────────────────────────────

    private Instance ParentWithActiveSubflow(Guid parentId, Guid subflowInstanceId) =>
        WithCorrelation(Instance.Create(parentId, "test-flow", "1.0.0"), parentId, subflowInstanceId);

    private static Instance WithCorrelation(Instance instance, Guid parentId, Guid subflowInstanceId)
    {
        instance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), parentId, "state-waiting", subflowInstanceId, "S", "sub-domain", "sub-flow", "1.0.0"));
        return instance;
    }

    [Fact]
    public async Task MarkBusyWithPropagationAsync_ShouldStampAncestorWithTheLeafStatusTheWalkReported()
    {
        // A parent inside an active SubFlow is already Busy and its own row never moves, so the
        // leaf's flip is invisible to anything validating against the parent. The stamp is what
        // makes it visible — and the value must be the one the walk brought back, not an assumption.
        var parentId = Guid.NewGuid();
        var leafId = Guid.NewGuid();

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(parentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ParentWithActiveSubflow(parentId, leafId));

        _instanceCommandGateway
            .Setup(g => g.MarkBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MarkBusyOutput>.Ok(
                new MarkBusyOutput { EffectiveStatusCode = InstanceStatus.Busy.Code }));

        var reported = await CreateSut().MarkBusyWithPropagationAsync(parentId);

        reported.ShouldBe(InstanceStatus.Busy);
        _instanceRepository.Verify(
            r => r.SetEffectiveStatusAsync(parentId, InstanceStatus.Busy, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenTheFarSideReportsNoStatus_ShouldWriteNothing()
    {
        // A cross-domain hop whose far side predates the status answer returns an empty body. That
        // is "unknown", and guessing Busy there would park a client on a chain that may be at rest.
        var parentId = Guid.NewGuid();
        var leafId = Guid.NewGuid();

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(parentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ParentWithActiveSubflow(parentId, leafId));

        _instanceCommandGateway
            .Setup(g => g.MarkBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MarkBusyOutput>.Ok(MarkBusyOutput.None));

        var reported = await CreateSut().MarkBusyWithPropagationAsync(parentId);

        reported.ShouldBeNull();
        _instanceRepository.Verify(
            r => r.SetEffectiveStatusAsync(It.IsAny<Guid>(), It.IsAny<InstanceStatus>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenLeafHasNoSubflow_ShouldWriteItsOwnStatusWithTheFlip()
    {
        // The bottom of the chain owns the visible status, so the projection rides along in the CAS
        // rather than costing a second statement.
        var leafId = Guid.NewGuid();
        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(leafId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Instance.Create(leafId, "test-flow", "1.0.0"));
        _instanceRepository
            .Setup(r => r.TryMarkBusyAsync(leafId, It.IsAny<CancellationToken>(), It.IsAny<InstanceStatus?>()))
            .ReturnsAsync(true);

        var reported = await CreateSut().MarkBusyWithPropagationAsync(leafId);

        reported.ShouldBe(InstanceStatus.Busy);
        _instanceRepository.Verify(
            r => r.TryMarkBusyAsync(leafId, It.IsAny<CancellationToken>(), InstanceStatus.Busy), Times.Once);
        _instanceRepository.Verify(
            r => r.SetEffectiveStatusAsync(It.IsAny<Guid>(), It.IsAny<InstanceStatus>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReleaseWithPropagationAsync_ShouldPutTheAncestorProjectionBack()
    {
        // Compensation: the reserve stamped Busy down the chain, so undoing it must clear the
        // ancestor's projection too or it keeps reporting a reservation that no longer exists.
        var parentId = Guid.NewGuid();
        var leafId = Guid.NewGuid();

        _instanceRepository
            .Setup(r => r.FindWithActiveSubFlowAsync(parentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ParentWithActiveSubflow(parentId, leafId));
        _instanceCommandGateway
            .Setup(g => g.ReleaseBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        await CreateSut().ReleaseWithPropagationAsync(parentId);

        _instanceRepository.Verify(
            r => r.SetEffectiveStatusAsync(parentId, InstanceStatus.Active, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
