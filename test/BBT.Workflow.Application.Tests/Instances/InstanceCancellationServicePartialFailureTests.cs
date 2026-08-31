using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Covers <see cref="InstanceCancellationService.ProcessCancellationAsync"/> when one or more
/// per-job scheduler cancellations throw. With the Inbox as the sole processor for the
/// canceled/completed-cleanup/faulted-cleanup events that funnel through this method, a silent
/// <c>Result.Ok()</c> here would ACK the delivery and strand the uncancelled scheduler job
/// forever. The winners must still persist (no re-cancel of already-settled jobs on retry), and
/// the method must surface a retryable failure so the Inbox redelivers and only the still-active
/// jobs are retried.
/// </summary>
public sealed class InstanceCancellationServicePartialFailureTests
{
    private readonly Mock<IInstanceRepository> _instanceRepository = new();
    private readonly Mock<IInstanceJobRepository> _instanceJobRepository = new();
    private readonly Mock<IBackgroundJobService> _backgroundJobService = new();
    private readonly Mock<IResourceLockService> _resourceLockService = new();
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();

    private readonly Instance _instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");

    public InstanceCancellationServicePartialFailureTests()
    {
        _instance.ChangeState(StateFactory.CreateDefault("state-a", StateType.Intermediate));
        _instanceRepository
            .Setup(r => r.FindAsync(_instance.Id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_instance);
    }

    [Fact]
    public async Task PartialSchedulerFailure_Returns_RetryableFail_And_Persists_Winners()
    {
        var jobA = CreateInstanceJob();
        var jobB = CreateInstanceJob();
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { jobA, jobB });
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(jobA.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.Cancelled);
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(jobB.JobId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("scheduler unavailable"));

        var result = await CreateService().ProcessCancellationAsync(_instance.Id);

        result.IsSuccess.ShouldBeFalse();
        _instanceJobRepository.Verify(r => r.MarkManyAsProcessedAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(jobA.Id)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Retry_Run_Only_Touches_Remaining_Jobs()
    {
        // Simulates the redelivery after the first (partial-failure) attempt: job A already
        // settled and is no longer active, so only job B is returned this time.
        var jobB = CreateInstanceJob();
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { jobB });
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(jobB.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.Cancelled);

        var result = await CreateService().ProcessCancellationAsync(_instance.Id);

        result.IsSuccess.ShouldBeTrue();
        _instanceJobRepository.Verify(r => r.MarkManyAsProcessedAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(jobB.Id)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AllJobsCancelled_Returns_Ok()
    {
        var jobA = CreateInstanceJob();
        var jobB = CreateInstanceJob();
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { jobA, jobB });
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(jobA.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.Cancelled);
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(jobB.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.Cancelled);

        var result = await CreateService().ProcessCancellationAsync(_instance.Id);

        result.IsSuccess.ShouldBeTrue();
        _instanceJobRepository.Verify(r => r.MarkManyAsProcessedAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(jobA.Id) && ids.Contains(jobB.Id)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private InstanceJob CreateInstanceJob() =>
        InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForScheduledTransition(_instance.Id, "state-a", "check", Guid.NewGuid()),
            Guid.NewGuid(),
            "bank",
            "flow",
            _instance.Id);

    private InstanceCancellationService CreateService()
        => new(
            _instanceRepository.Object,
            _instanceJobRepository.Object,
            _backgroundJobService.Object,
            _resourceLockService.Object,
            _uowManager.Object,
            NullLogger<InstanceCancellationService>.Instance);
}
