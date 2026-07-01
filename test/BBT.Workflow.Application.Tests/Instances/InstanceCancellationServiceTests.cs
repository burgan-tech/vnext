using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for <see cref="InstanceCancellationService.ProcessStateTransitionsCancellationAsync"/>,
/// focused on source-state-scoped matching so a same-named transition on another state's timer is
/// not cancelled when leaving a different state.
/// </summary>
public sealed class InstanceCancellationServiceTests
{
    private readonly Mock<IInstanceRepository> _instanceRepository = new();
    private readonly Mock<IInstanceJobRepository> _instanceJobRepository = new();
    private readonly Mock<IBackgroundJobService> _backgroundJobService = new();
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<ILogger<InstanceCancellationService>> _logger = new();

    private readonly Instance _instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");

    public InstanceCancellationServiceTests()
    {
        _uow.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _uow.Setup(u => u.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _uowManager.Setup(m => m.Begin(It.IsAny<UnitOfWorkOptions>())).Returns(_uow.Object);
        _instance.ChangeState(StateFactory.CreateDefault("state-a", StateType.Intermediate));
        _instanceRepository
            .Setup(r => r.FindAsync(_instance.Id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_instance);
    }

    [Fact]
    public async Task DoesNotCancel_SameKey_DifferentSourceState()
    {
        // A "check" scheduled job owned by state-c must survive when we leave state-a.
        var job = InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForScheduledTransition(_instance.Id, "state-c", "check"),
            Guid.NewGuid(),
            "bank",
            "flow",
            _instance.Id);
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { job });

        var result = await CreateService().ProcessStateTransitionsCancellationAsync(
            _instance.Id, "state-a", new[] { "check" }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _backgroundJobService.Verify(
            b => b.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Cancels_MatchingSourceStateAndKey()
    {
        var job = InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForScheduledTransition(_instance.Id, "state-a", "check"),
            Guid.NewGuid(),
            "bank",
            "flow",
            _instance.Id);
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { job });

        var result = await CreateService().ProcessStateTransitionsCancellationAsync(
            _instance.Id, "state-a", new[] { "check" }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _backgroundJobService.Verify(
            b => b.DeleteAsync(job.JobId, It.IsAny<CancellationToken>()),
            Times.Once);
        job.IsActive.ShouldBeFalse();
    }

    private InstanceCancellationService CreateService()
        => new(
            _instanceRepository.Object,
            _instanceJobRepository.Object,
            _backgroundJobService.Object,
            _uowManager.Object,
            _logger.Object);
}
