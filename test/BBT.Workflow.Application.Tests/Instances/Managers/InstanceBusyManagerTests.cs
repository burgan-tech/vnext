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
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0");

        _instanceRepository
            .Setup(r => r.GetResultAsync(instanceId.ToString(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok(instance));

        _instanceRepository
            .Setup(r => r.UpdateAsync(instance, false, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(instance));

        // Act
        await CreateSut().MarkBusyAsync(instanceId);

        // Assert — UoW opened with RequiresNew, update + commit called
        _uowManager.Verify(m => m.Begin(It.Is<UnitOfWorkOptions>(o =>
            o.Scope == UnitOfWorkScopeOption.RequiresNew)), Times.Once);
        _instanceRepository.Verify(r => r.UpdateAsync(instance, false, It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        instance.IsBusy.ShouldBeTrue();
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
            .Setup(r => r.UpdateAsync(instance, false, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(instance));

        // Act
        await CreateSut().MarkBusyWithPropagationAsync(instanceId);

        // Assert — UoW opened, gateway NOT called
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _instanceCommandGateway.Verify(g => g.MarkBusyAsync(It.IsAny<MarkBusyInput>(), It.IsAny<CancellationToken>()), Times.Never);
        instance.IsBusy.ShouldBeTrue();
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
            .ReturnsAsync(BBT.Aether.Results.Result.Ok());

        // Act
        await CreateSut().MarkBusyWithPropagationAsync(instanceId);

        // Assert — gateway called with the subflow's InstanceId
        _instanceCommandGateway.Verify(g => g.MarkBusyAsync(
            It.Is<MarkBusyInput>(i => i.InstanceId == subflowInstanceId),
            It.IsAny<CancellationToken>()), Times.Once);
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
            .Setup(r => r.UpdateAsync(instance, false, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(instance));

        // Act
        var outcome = await CreateSut().TryMarkBusyWithPropagationAsync(instanceId);

        // Assert
        outcome.ShouldBe(BusyMarkOutcome.Marked);
        _uowManager.Verify(m => m.Begin(It.Is<UnitOfWorkOptions>(o =>
            o.Scope == UnitOfWorkScopeOption.RequiresNew)), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        instance.IsBusy.ShouldBeTrue();
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
            .Setup(r => r.GetResultAsync(leafId.ToString(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Instance>.Ok(leaf));
        _instanceRepository
            .Setup(r => r.UpdateAsync(leaf, false, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(leaf));

        await CreateSut().ReleaseWithPropagationAsync(leafId);

        leaf.Status.ShouldBe(InstanceStatus.Active);
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
}
