using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.BackgroundJobs.Recovery;

public sealed class ChainReaperServiceTests
{
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IInstanceRepository> _instanceRepository = new();
    private readonly Mock<IInstanceJobRepository> _jobRepository = new();
    private readonly Mock<IRuntimeInfoProvider> _runtimeInfo = new();
    private readonly Mock<ILogger<ChainReaperService>> _logger = new();

    public ChainReaperServiceTests()
    {
        _uowManager
            .Setup(manager => manager.Begin(It.IsAny<UnitOfWorkOptions>()))
            .Returns(_uow.Object);
        _uow
            .Setup(unit => unit.CommitAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _uow
            .Setup(unit => unit.DisposeAsync())
            .Returns(ValueTask.CompletedTask);
        _runtimeInfo.SetupGet(provider => provider.Domain).Returns("core");
    }

    [Fact]
    public async Task SweepAsync_WhenNoLiveAsyncJobExists_ShouldFaultStuckBusyInstance()
    {
        var candidate = CreateBusyInstance();
        _instanceRepository
            .Setup(repository => repository.GetStuckBusyChainsAsync(
                It.IsAny<DateTime>(), 100, CancellationToken.None))
            .ReturnsAsync([candidate]);
        _jobRepository
            .Setup(repository => repository.GetInstanceIdsWithActiveJobAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                CancellationToken.None))
            .ReturnsAsync([]);
        _instanceRepository
            .Setup(repository => repository.UpdateAsync(
                candidate, true, CancellationToken.None))
            .ReturnsAsync(candidate);

        var result = await CreateService().SweepAsync(CancellationToken.None);

        result.ShouldBe(1);
        candidate.Status.ShouldBe(InstanceStatus.Faulted);
        candidate.HasActiveIncident.ShouldBeTrue();
        _instanceRepository.Verify(repository => repository.UpdateAsync(
                candidate, true, CancellationToken.None),
            Times.Once);
        _uow.Verify(unit => unit.CommitAsync(CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_WhenLiveAsyncJobExists_ShouldLeaveInstanceUntouched()
    {
        var candidate = CreateBusyInstance();
        _instanceRepository
            .Setup(repository => repository.GetStuckBusyChainsAsync(
                It.IsAny<DateTime>(), 100, CancellationToken.None))
            .ReturnsAsync([candidate]);
        _jobRepository
            .Setup(repository => repository.GetInstanceIdsWithActiveJobAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                CancellationToken.None))
            .ReturnsAsync([candidate.Id]);

        var result = await CreateService().SweepAsync(CancellationToken.None);

        result.ShouldBe(0);
        candidate.Status.ShouldBe(InstanceStatus.Busy);
        _instanceRepository.Verify(repository => repository.UpdateAsync(
                It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _uow.Verify(unit => unit.CommitAsync(CancellationToken.None), Times.Once);
    }

    private ChainReaperService CreateService()
        => new(
            _uowManager.Object,
            _instanceRepository.Object,
            _jobRepository.Object,
            _runtimeInfo.Object,
            Options.Create(new WorkflowExecutionOptions { TransitionJobTimeoutSeconds = 300 }),
            _logger.Object);

    private static Instance CreateBusyInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "key");
        instance.BeginChain(Guid.NewGuid());
        return instance;
    }
}
