using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
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
    private readonly Mock<IResourceLockService> _resourceLockService = new();
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly CapturingLogger _logger = new();

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
            JobName.ForScheduledTransition(_instance.Id, "state-c", "check", Guid.NewGuid()),
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
            b => b.CancelWaitingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Cancels_MatchingSourceStateAndKey()
    {
        var job = InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForScheduledTransition(_instance.Id, "state-a", "check", Guid.NewGuid()),
            Guid.NewGuid(),
            "bank",
            "flow",
            _instance.Id);
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { job });
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(job.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.Cancelled);

        var result = await CreateService().ProcessStateTransitionsCancellationAsync(
            _instance.Id, "state-a", new[] { "check" }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _backgroundJobService.Verify(
            b => b.CancelWaitingAsync(job.JobId, It.IsAny<CancellationToken>()),
            Times.Once);
        // The row closes via the batched set-based settle, not an in-memory entity mutation.
        _instanceJobRepository.Verify(r => r.MarkManyAsProcessedAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(job.Id)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessStateTransitionsCancellation_running_job_remains_active()
    {
        var job = CreateInstanceJob();
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { job });
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(job.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.SkippedRunning);

        var result = await CreateService().ProcessStateTransitionsCancellationAsync(
            _instance.Id, "state-a", new[] { "check" }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        job.IsActive.ShouldBeTrue();
        // A running job is never in the batched settle.
        _instanceJobRepository.Verify(r => r.MarkManyAsProcessedAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessCancellation_running_job_remains_active()
    {
        var job = CreateInstanceJob();
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { job });
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(job.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.SkippedRunning);

        var result = await CreateService().ProcessCancellationAsync(_instance.Id);

        result.IsSuccess.ShouldBeTrue();
        job.IsActive.ShouldBeTrue();
        // A running job is never in the batched settle.
        _instanceJobRepository.Verify(r => r.MarkManyAsProcessedAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(BackgroundJobCancellationResult.Cancelled)]
    [InlineData(BackgroundJobCancellationResult.AlreadyTerminal)]
    [InlineData(BackgroundJobCancellationResult.NotFound)]
    public async Task ProcessCancellation_non_running_outcomes_close_tracking(
        BackgroundJobCancellationResult outcome)
    {
        var job = CreateInstanceJob();
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { job });
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(job.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var result = await CreateService().ProcessCancellationAsync(_instance.Id);

        result.IsSuccess.ShouldBeTrue();
        _instanceJobRepository.Verify(r => r.MarkManyAsProcessedAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(job.Id)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessCancellation_mixed_jobs_reports_only_processed_count()
    {
        var failed = CreateInstanceJob("failed");
        var waiting = CreateInstanceJob("waiting");
        var running = CreateInstanceJob("running");
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { failed, waiting, running });
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(failed.JobId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cancel failed"));
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(waiting.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.Cancelled);
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(running.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.SkippedRunning);

        var result = await CreateService().ProcessCancellationAsync(_instance.Id);

        // A per-job scheduler failure is now retryable (Result.Fail), not silently ACKed: the
        // Inbox is the sole processor for cleanup events, so an IsSuccess=true here would ACK the
        // message and strand the still-active "failed" job's scheduler entry forever. The winner
        // ("waiting") is still persisted below before the failure is reported.
        result.IsSuccess.ShouldBeFalse();
        failed.IsActive.ShouldBeTrue();
        running.IsActive.ShouldBeTrue();
        _instanceJobRepository.Verify(r => r.MarkManyAsProcessedAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(waiting.Id)),
            It.IsAny<CancellationToken>()), Times.Once);
        _logger.SingleMessage(40019)
            .ShouldBe($"Processed 1 instance cancellation jobs for instance {_instance.Id}");
    }

    [Fact]
    public async Task ProcessStateTransitionsCancellation_mixed_jobs_reports_only_processed_count()
    {
        var failed = CreateInstanceJob("failed");
        var waiting = CreateInstanceJob("waiting");
        var running = CreateInstanceJob("running");
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob> { failed, waiting, running });
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(failed.JobId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cancel failed"));
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(waiting.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.Cancelled);
        _backgroundJobService
            .Setup(s => s.CancelWaitingAsync(running.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackgroundJobCancellationResult.SkippedRunning);

        var result = await CreateService().ProcessStateTransitionsCancellationAsync(
            _instance.Id,
            "state-a",
            new[] { "failed", "waiting", "running" },
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        failed.IsActive.ShouldBeTrue();
        running.IsActive.ShouldBeTrue();
        _instanceJobRepository.Verify(r => r.MarkManyAsProcessedAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(waiting.Id)),
            It.IsAny<CancellationToken>()), Times.Once);
        _logger.SingleMessage(10058).ShouldBe(
            $"Processed 1 scheduled jobs for instance {_instance.Id}, transitions: failed, waiting, running");
    }

    [Fact]
    public async Task ProcessCancellation_releases_all_tracked_resource_locks_even_without_jobs()
    {
        // Auto-release must run on the terminal path regardless of whether the instance has jobs.
        _instance.TrackResourceLock("limit:scope:2026-07-24");
        _instance.TrackResourceLock("seat:A1");
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob>());

        var result = await CreateService().ProcessCancellationAsync(_instance.Id);

        result.IsSuccess.ShouldBeTrue();
        _resourceLockService.Verify(
            s => s.ReleaseAsync("limit:scope:2026-07-24", _instance.Id.ToString(), It.IsAny<CancellationToken>()),
            Times.Once);
        _resourceLockService.Verify(
            s => s.ReleaseAsync("seat:A1", _instance.Id.ToString(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessCancellation_lockReleaseThrows_stillCompletesCleanup()
    {
        // Releasing is best-effort: an exception from the lock store must not fail terminal cleanup.
        _instance.TrackResourceLock("k1");
        _resourceLockService
            .Setup(s => s.ReleaseAsync("k1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("lock store unavailable"));
        _instanceJobRepository
            .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstanceJob>());

        var result = await CreateService().ProcessCancellationAsync(_instance.Id);

        result.IsSuccess.ShouldBeTrue();
    }

    private InstanceJob CreateInstanceJob(string transition = "check") =>
        InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForScheduledTransition(_instance.Id, "state-a", transition, Guid.NewGuid()),
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
            _logger);

    private sealed class CapturingLogger : ILogger<InstanceCancellationService>
    {
        private readonly List<(EventId EventId, string Message)> _entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add((eventId, formatter(state, exception)));
        }

        public string SingleMessage(int eventId)
        {
            var entries = _entries.Where(entry => entry.EventId.Id == eventId).ToList();
            entries.Count.ShouldBe(1);
            return entries[0].Message;
        }
    }
}
