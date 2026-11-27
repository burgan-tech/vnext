using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Uow;
using BBT.Workflow.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// vnext#1252: a fault's cleanup (<c>InstanceFaultedCleanupEvent</c> → Inbox → <c>cancel-cleanup</c>) can arrive after
/// a retry or the reserved cancel has already taken the instance out of that fault and enqueued its own job — measured
/// in the full integration suite, where the late cleanup swept an async cancel's job and the instance stayed Faulted.
/// <see cref="InstanceCancellationService.ProcessFaultCleanupAsync"/> cleans up only while the instance still rests in
/// the fault that raised it.
/// </summary>
public sealed class InstanceCancellationServiceFaultCleanupTests
{
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceJobRepository _jobRepository = Substitute.For<IInstanceJobRepository>();
    private readonly IBackgroundJobService _backgroundJobService = Substitute.For<IBackgroundJobService>();
    private readonly IResourceLockService _resourceLockService = Substitute.For<IResourceLockService>();
    private readonly Instance _instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
    private readonly InstanceJob _job;

    public InstanceCancellationServiceFaultCleanupTests()
    {
        _instanceRepository.FindAsync(_instance.Id, true, Arg.Any<CancellationToken>()).Returns(_instance);
        _job = InstanceJob.Create(
            Guid.NewGuid(), JobName.ForTimeout(_instance.Id), Guid.NewGuid(), "core", "flow", _instance.Id);
        _jobRepository.GetListActiveAsync(_instance.Id, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceJob> { _job });
        _backgroundJobService.CancelWaitingAsync(_job.JobId, Arg.Any<CancellationToken>())
            .Returns(BackgroundJobCancellationResult.Cancelled);
        _instance.TrackResourceLock("limit:branch-7");
    }

    [Fact]
    public async Task TheCurrentFault_IsCleanedUp()
    {
        _instance.Fault("core");
        // The event's FaultedAt has the clock's full precision; the column keeps microseconds.
        var faultedAt = _instance.CompletedAt!.Value.AddTicks(7);

        var result = await Sut().ProcessFaultCleanupAsync(_instance.Id, faultedAt, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _backgroundJobService.Received(1).CancelWaitingAsync(_job.JobId, Arg.Any<CancellationToken>());
        await _resourceLockService.Received(1).ReleaseAsync("limit:branch-7", _instance.Id.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AfterARetryOrCancelTookTheInstanceOver_NothingIsTouched()
    {
        _instance.Fault("core");
        var faultedAt = _instance.CompletedAt!.Value;
        _instance.Unfault();
        _instance.Busy();

        var result = await Sut().ProcessFaultCleanupAsync(_instance.Id, faultedAt, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _jobRepository.DidNotReceive().GetListActiveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _backgroundJobService.DidNotReceiveWithAnyArgs().CancelWaitingAsync(default, default);
        await _jobRepository.DidNotReceiveWithAnyArgs().MarkManyAsProcessedAsync(default!, default);
        // The lock now belongs to the run that took the instance over.
        await _resourceLockService.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default!, default);
    }

    [Fact]
    public async Task AfterTheInstanceFaultedAgain_TheEarlierFaultsCleanupTouchesNothing()
    {
        _instance.Fault("core");
        var earlierFault = _instance.CompletedAt!.Value.AddSeconds(-30);

        var result = await Sut().ProcessFaultCleanupAsync(_instance.Id, earlierFault, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _backgroundJobService.DidNotReceiveWithAnyArgs().CancelWaitingAsync(default, default);
    }

    [Fact]
    public async Task AfterTheCancelCompletedTheInstance_TheFaultsCleanupTouchesNothing()
    {
        // The cancel's own InstanceCanceledEvent cleans up the completed instance.
        _instance.Fault("core");
        var faultedAt = _instance.CompletedAt!.Value;
        _instance.Unfault();
        _instance.Cancel("core");

        var result = await Sut().ProcessFaultCleanupAsync(_instance.Id, faultedAt, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _backgroundJobService.DidNotReceiveWithAnyArgs().CancelWaitingAsync(default, default);
    }

    [Fact]
    public async Task ACleanupWithoutAFaultStamp_StillSweepsAsBefore()
    {
        // Completed and canceled cleanups, and an older Inbox, send no FaultedAt.
        var result = await Sut().ProcessCancellationAsync(_instance.Id, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _backgroundJobService.Received(1).CancelWaitingAsync(_job.JobId, Arg.Any<CancellationToken>());
    }

    private InstanceCancellationService Sut() => new(
        _instanceRepository,
        _jobRepository,
        _backgroundJobService,
        _resourceLockService,
        Substitute.For<IUnitOfWorkManager>(),
        NullLogger<InstanceCancellationService>.Instance);
}
