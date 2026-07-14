using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
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

        // Assert — no UoW opened, no throw
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Never);
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
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Never);
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
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Never);
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
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Never);
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

        // Assert — short-circuits: no UoW, no gateway propagation
        outcome.ShouldBe(BusyMarkOutcome.AlreadyBusy);
        _uowManager.Verify(m => m.Begin(It.IsAny<UnitOfWorkOptions>()), Times.Never);
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
}
