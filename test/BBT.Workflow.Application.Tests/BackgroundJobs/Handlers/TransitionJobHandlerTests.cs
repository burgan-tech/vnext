using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BBT.Workflow.Application.Tests.BackgroundJobs.Handlers;

public class TransitionJobHandlerTests
{
    private readonly Mock<IInstanceJobRepository> _jobRepo = new();
    private readonly Mock<IWorkflowExecutionService> _executionService = new();
    private readonly Mock<ICurrentSchema> _currentSchema = new();
    private readonly Mock<IJobTimeoutRecoveryService> _recoveryService = new();
    private readonly Mock<IHostApplicationLifetime> _hostLifetime = new();
    private readonly Mock<ILogger<TransitionJobHandler>> _logger = new();
    private readonly CancellationTokenSource _appStoppingCts = new();

    public TransitionJobHandlerTests()
    {
        _hostLifetime.Setup(h => h.ApplicationStopping).Returns(_appStoppingCts.Token);
        _hostLifetime.Setup(h => h.ApplicationStarted).Returns(CancellationToken.None);
        _hostLifetime.Setup(h => h.ApplicationStopped).Returns(CancellationToken.None);

        _jobRepo
            .Setup(r => r.MarkAsProcessedAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _jobRepo
            .Setup(r => r.MarkAsProcessedByJobIdAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _jobRepo
            .Setup(r => r.MarkAsFailedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _jobRepo
            .Setup(r => r.MarkAsSupersededAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _jobRepo
            .Setup(r => r.IsClaimOwnerAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _jobRepo
            .Setup(r => r.ReleaseClaimAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _jobRepo
            .Setup(r => r.FindByJobIdAsReadOnlyAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid jobId, CancellationToken _) => InstanceJob.Create(
                jobId,
                JobName.ForAsyncTransition(Guid.NewGuid(), "state", "transition"),
                jobId,
                "domain",
                "flow",
                Guid.NewGuid()));

        _recoveryService
            .Setup(r => r.FaultInstanceAsync(
                It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _recoveryService
            .Setup(r => r.FaultInstanceAsync(
                It.IsAny<TransitionJobPayload>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private TransitionJobHandler CreateHandler(
        int timeoutSeconds = 300,
        LockConflictRetryOptions? lockConflictRetry = null)
    {
        var options = Options.Create(new WorkflowExecutionOptions
        {
            TransitionJobTimeoutSeconds = timeoutSeconds,
            // 1ms backoff keeps retry tests fast without changing the retry logic under test.
            LockConflictRetry = lockConflictRetry ?? new LockConflictRetryOptions { BaseDelayMilliseconds = 1 }
        });
        return new TransitionJobHandler(
            _jobRepo.Object, _executionService.Object, _currentSchema.Object,
            _recoveryService.Object, options, _hostLifetime.Object, _logger.Object);
    }

    private static Result<TransitionOutput> LockConflictResult()
        => Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(Guid.NewGuid()));

    private static TransitionJobPayload CreatePayload(Guid? instanceId = null) => new()
    {
        JobName = "trans-abc-go",
        InstanceId = instanceId ?? Guid.NewGuid(),
        TransitionKey = "go",
        Domain = "test",
        Workflow = "test-flow",
        Version = "1.0.0"
    };

    private static InstanceJob CreateDurableJob(TransitionJobPayload payload)
    {
        var jobName = JobName.ForAsyncTransition(payload.InstanceId, "state", payload.TransitionKey);
        payload.JobName = jobName.Value;
        var admissionToken = payload.AdmissionToken ?? payload.ChainToken ?? Guid.NewGuid();
        payload.AdmissionToken ??= admissionToken;
        payload.ChainToken ??= admissionToken;
        var job = InstanceJob.CreateTransitionAdmission(
            payload.JobId,
            jobName,
            payload.JobId,
            payload.Domain,
            payload.Workflow,
            payload.InstanceId,
            JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
            admissionToken,
            payload.AdmittedRevision);
        job.MarkAsScheduled();
        return job;
    }

    private void SetupDurableJob(TransitionJobPayload payload)
    {
        var job = CreateDurableJob(payload);
        _jobRepo
            .Setup(r => r.FindByJobIdAsReadOnlyAsync(
                payload.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
    }

    [Fact]
    public async Task HandleAsync_WhenActiveClaimLeaseIsHeld_ThrowsForDispatcherRetry()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        var handler = CreateHandler();

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(payload, CancellationToken.None));

        _jobRepo.Verify(
            r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), TimeSpan.FromSeconds(330), CancellationToken.None),
            Times.Once);
        _executionService.Verify(
            s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.MarkAsFailedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.MarkAsSupersededAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _recoveryService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_WhenClaimedJobIsAlreadyTerminal_IsIdempotentNoOp()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        var handler = CreateHandler();
        var terminalJob = InstanceJob.Create(
            payload.JobId,
            JobName.ForAsyncTransition(payload.InstanceId, "state", payload.TransitionKey),
            payload.JobId,
            payload.Domain,
            payload.Workflow,
            payload.InstanceId);
        terminalJob.MarkAsProcessed();

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _jobRepo
            .Setup(r => r.FindByJobIdAsReadOnlyAsync(
                payload.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(terminalJob);

        await handler.HandleAsync(payload, CancellationToken.None);

        _executionService.Verify(
            s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.MarkAsProcessedByJobIdAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _recoveryService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_WhenJobArrivesBeforeAdmissionCommit_RetriesMissingClaim()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        var durableJob = CreateDurableJob(payload);
        var handler = CreateHandler();

        _jobRepo
            .SetupSequence(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);
        _jobRepo
            .SetupSequence(r => r.FindByJobIdAsReadOnlyAsync(
                payload.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstanceJob?)null)
            .ReturnsAsync(durableJob);
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Status = InstanceStatus.Active
            }));

        await handler.HandleAsync(payload, CancellationToken.None);

        _jobRepo.Verify(
            r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), TimeSpan.FromSeconds(330), CancellationToken.None),
            Times.Exactly(2));
        _jobRepo.Verify(
            r => r.FindByJobIdAsReadOnlyAsync(payload.JobId, CancellationToken.None),
            Times.Exactly(2));
        _executionService.Verify(
            s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsProcessedByJobIdAsync(
                payload.JobId, It.IsAny<Guid>(), CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenClaimSucceedsThenJobIsRedelivered_ExecutesAndFinalizesOnlyOnce()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        var durableJob = CreateDurableJob(payload);
        var handler = CreateHandler();

        _jobRepo
            .SetupSequence(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        var terminalJob = InstanceJob.Create(
            payload.JobId,
            JobName.ForAsyncTransition(payload.InstanceId, "state", payload.TransitionKey),
            payload.JobId,
            payload.Domain,
            payload.Workflow,
            payload.InstanceId);
        terminalJob.MarkAsProcessed();
        _jobRepo
            .SetupSequence(r => r.FindByJobIdAsReadOnlyAsync(
                payload.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(durableJob)
            .ReturnsAsync(terminalJob);
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Status = InstanceStatus.Active
            }));

        await handler.HandleAsync(payload, CancellationToken.None);
        await handler.HandleAsync(payload, CancellationToken.None);

        _jobRepo.Verify(
            r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), CancellationToken.None),
            Times.Exactly(2));
        _executionService.Verify(
            s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsProcessedByJobIdAsync(
                payload.JobId, It.IsAny<Guid>(), CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsFailedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.MarkAsSupersededAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Own 300s budget expires (executionCts fires) → recovery must run.
    /// Simulated by setting timeout to 0 seconds so the CTS fires immediately.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenOwnBudgetExpires_CallsRecovery()
    {
        var payload = CreatePayload();
        var handler = CreateHandler(timeoutSeconds: 0);

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<WorkflowExecutionContext, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Result<TransitionOutput>.Ok(new TransitionOutput());
            });

        await handler.HandleAsync(payload, CancellationToken.None);

        _recoveryService.Verify(
            r => r.FaultInstanceAsync(payload, CancellationToken.None),
            Times.Once);
    }

    /// <summary>
    /// Dapr HTTP timeout or external cancellation while the host is still up → recovery must run.
    /// Simulated by passing a pre-cancelled outer token with a long job budget (won't expire).
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenDaprCancels_AndHostNotStopping_CallsRecovery()
    {
        var payload = CreatePayload();
        var handler = CreateHandler(timeoutSeconds: 300);
        using var outerCts = new CancellationTokenSource();
        outerCts.Cancel();

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<WorkflowExecutionContext, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Result<TransitionOutput>.Ok(new TransitionOutput());
            });

        await handler.HandleAsync(payload, outerCts.Token);

        _recoveryService.Verify(
            r => r.FaultInstanceAsync(payload, CancellationToken.None),
            Times.Once);
    }

    /// <summary>
    /// Host shutdown (SIGTERM) releases the fenced app claim and throws a non-cancellation
    /// exception so Aether records a retry instead of acknowledging the delivery as Completed.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenHostIsShuttingDown_ReleasesClaimAndSignalsDispatcherRetry()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        SetupDurableJob(payload);
        var handler = CreateHandler(timeoutSeconds: 300);
        var claimedToken = Guid.Empty;
        var executionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, TimeSpan, CancellationToken>((_, token, _, _) => claimedToken = token)
            .ReturnsAsync(true);

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<WorkflowExecutionContext, CancellationToken>(async (_, ct) =>
            {
                executionStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Result<TransitionOutput>.Ok(new TransitionOutput());
            });

        var handling = handler.HandleAsync(payload, CancellationToken.None);
        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _appStoppingCts.CancelAsync(); // Simulate SIGTERM without cancelling dispatcher token

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handling);

        Assert.DoesNotContain("OperationCanceled", exception.GetType().Name, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, claimedToken);
        _recoveryService.Verify(
            r => r.FaultInstanceAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.ReleaseClaimAsync(payload.JobId, claimedToken, CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsProcessedByJobIdAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Successful pipeline execution → no recovery needed.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenExecutionSucceeds_DoesNotCallRecovery()
    {
        var payload = CreatePayload();
        var handler = CreateHandler();

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Status = InstanceStatus.Active
            }));

        await handler.HandleAsync(payload, CancellationToken.None);

        _recoveryService.Verify(
            r => r.FaultInstanceAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenExecutionSucceeds_FinalizesWithTheClaimToken()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        SetupDurableJob(payload);
        var claimedToken = Guid.Empty;

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, TimeSpan, CancellationToken>((_, token, _, _) => claimedToken = token)
            .ReturnsAsync(true);
        _executionService
            .Setup(service => service.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Status = InstanceStatus.Active
            }));

        await CreateHandler().HandleAsync(payload, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, claimedToken);
        _jobRepo.Verify(repository => repository.MarkAsProcessedByJobIdAsync(
                payload.JobId,
                claimedToken,
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenClaimOwnershipIsLostBeforeRecovery_SkipsSideEffectsAndRetries()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        SetupDurableJob(payload);

        _jobRepo
            .Setup(repository => repository.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _jobRepo
            .Setup(repository => repository.IsClaimOwnerAsync(
                payload.JobId, It.IsAny<Guid>(), CancellationToken.None))
            .ReturnsAsync(false);
        _executionService
            .Setup(service => service.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(
                Error.Failure("TRANSIENT", "execution failed")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandler().HandleAsync(payload, CancellationToken.None));

        _recoveryService.Verify(
            service => service.FaultInstanceAsync(
                It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _recoveryService.Verify(
            service => service.FaultInstanceAsync(
                It.IsAny<TransitionJobPayload>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(repository => repository.MarkAsFailedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Every non-success pipeline result is terminal for the claimed job. It must run recovery
    /// and persist the original error on the durable job, not only handle lock conflicts.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenPipelineReturnsNonLockFailure_RecoversAndMarksJobFailed()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        SetupDurableJob(payload);
        var handler = CreateHandler();

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(
                Error.Validation("Transition:NotFound", "Transition not found")));

        await handler.HandleAsync(payload, CancellationToken.None);

        _recoveryService.Verify(
            r => r.FaultInstanceAsync(
                It.Is<TransitionJobPayload>(canonical => canonical.JobId == payload.JobId),
                "Transition not found", "Transition:NotFound", CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsFailedAsync(
                payload.JobId, It.IsAny<Guid>(), "Transition:NotFound", "Transition not found",
                CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.MarkAsSupersededAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenAdmittedRevisionIsStale_MarksJobSupersededWithoutFaultingInstance()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        payload.AdmittedRevision = 11;
        SetupDurableJob(payload);
        var handler = CreateHandler();

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(
                WorkflowErrors.InstanceRevisionConflict(payload.InstanceId, 11, 12)));

        await handler.HandleAsync(payload, CancellationToken.None);

        _recoveryService.Verify(
            r => r.FaultInstanceAsync(
                It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _recoveryService.Verify(
            r => r.FaultInstanceAsync(
                It.IsAny<TransitionJobPayload>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.MarkAsSupersededAsync(
                payload.JobId, It.IsAny<Guid>(), "Instance revision changed after admission",
                CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsFailedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ReconstructsAdmissionAndChainMetadataFromPayload()
    {
        var chainToken = Guid.NewGuid();
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        payload.AdmittedRevision = 23;
        payload.TransitionSchemaValidated = true;
        payload.TriggerType = TriggerType.Automatic;
        payload.IsReentry = true;
        payload.IsErrorBoundaryTransition = true;
        payload.ChainToken = chainToken;
        payload.ChainDepth = 7;
        payload.ExecutionActor = ExecutionActor.System;
        payload.CallerSync = false;
        SetupDurableJob(payload);
        var handler = CreateHandler();
        WorkflowExecutionContext? capturedContext = null;

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((context, _) => capturedContext = context)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Status = InstanceStatus.Active
            }));

        await handler.HandleAsync(payload, CancellationToken.None);

        Assert.NotNull(capturedContext);
        Assert.Equal(payload.AdmittedRevision, capturedContext.ExpectedRevision);
        Assert.True(capturedContext.TransitionSchemaValidated);
        Assert.Equal(payload.TriggerType, capturedContext.TriggerType);
        Assert.Equal(payload.IsReentry, capturedContext.IsReentry);
        Assert.Equal(payload.IsErrorBoundaryTransition, capturedContext.IsErrorBoundaryTransition);
        Assert.Equal(payload.ChainToken, capturedContext.ChainToken);
        Assert.NotNull(capturedContext.Execution);
        Assert.Equal(payload.ChainDepth, capturedContext.Execution.ChainDepth);
        Assert.Equal(payload.ExecutionActor, capturedContext.Actor);
        Assert.Equal(ExecMode.Async, capturedContext.CallerMode);
        _jobRepo.Verify(
            r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), TimeSpan.FromSeconds(330), CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsProcessedByJobIdAsync(
                payload.JobId, It.IsAny<Guid>(), CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithAlteredDeliveryBody_ExecutesCanonicalDurablePayload()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        payload.Data = JsonSerializer.SerializeToElement(new { source = "durable" });
        payload.Headers = new Dictionary<string, string?> { ["x-source"] = "durable" };
        payload.RouteValues = new Dictionary<string, string?> { ["route"] = "durable" };
        payload.ExecutionActor = ExecutionActor.System;
        payload.TriggerType = TriggerType.Automatic;
        payload.TransitionSchemaValidated = true;
        SetupDurableJob(payload);

        var canonicalInstanceId = payload.InstanceId;
        var canonicalDomain = payload.Domain;
        var canonicalTransition = payload.TransitionKey;

        // Simulate an altered Dapr callback body. Workflow remains the schema locator; every
        // execution-bearing field must be ignored in favor of InstanceJob.Payload.
        payload.InstanceId = Guid.NewGuid();
        payload.Domain = "attacker-domain";
        payload.TransitionKey = "attacker-transition";
        payload.JobName = "attacker-job";
        payload.Data = JsonSerializer.SerializeToElement(new { source = "delivery" });
        payload.Headers = new Dictionary<string, string?> { ["x-source"] = "delivery" };
        payload.RouteValues = new Dictionary<string, string?> { ["route"] = "delivery" };
        payload.ExecutionActor = ExecutionActor.User;
        payload.TriggerType = TriggerType.Manual;
        payload.TransitionSchemaValidated = false;

        WorkflowExecutionContext? capturedContext = null;
        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((context, _) => capturedContext = context)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Status = InstanceStatus.Active
            }));

        await CreateHandler().HandleAsync(payload, CancellationToken.None);

        Assert.NotNull(capturedContext);
        Assert.Equal(canonicalInstanceId.ToString(), capturedContext.InstanceId);
        Assert.Equal(canonicalDomain, capturedContext.Domain);
        Assert.Equal(canonicalTransition, capturedContext.TransitionKey);
        Assert.Equal(ExecutionActor.System, capturedContext.Actor);
        Assert.Equal(TriggerType.Automatic, capturedContext.TriggerType);
        Assert.True(capturedContext.TransitionSchemaValidated);
        Assert.Equal("durable", capturedContext.Headers["x-source"]);
        Assert.Equal("durable", capturedContext.RouteValues["route"]);
        Assert.Equal("durable", capturedContext.Data!.Attributes!.Value.GetProperty("source").GetString());
        _recoveryService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_WhenDurablePayloadIsMissing_FailsClosedAndRecoversReservedInstance()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.NewGuid();
        var jobName = JobName.ForAsyncTransition(payload.InstanceId, "state", payload.TransitionKey);
        payload.JobName = jobName.Value;
        var payloadlessJob = InstanceJob.Create(
            payload.JobId,
            jobName,
            payload.JobId,
            payload.Domain,
            payload.Workflow,
            payload.InstanceId);

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _jobRepo
            .Setup(r => r.FindByJobIdAsReadOnlyAsync(
                payload.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payloadlessJob);

        await CreateHandler().HandleAsync(payload, CancellationToken.None);

        _executionService.Verify(
            service => service.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _recoveryService.Verify(service => service.FaultInstanceAsync(
                It.Is<TransitionJobPayload>(recovery =>
                    recovery.JobId == payload.JobId
                    && recovery.InstanceId == payload.InstanceId
                    && recovery.Workflow == payload.Workflow),
                It.Is<string>(message => message.Contains("missing", StringComparison.OrdinalIgnoreCase)),
                "JOB_INVALID_DURABLE_PAYLOAD",
                CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(repository => repository.MarkAsFailedAsync(
                payload.JobId,
                It.IsAny<Guid>(),
                "JOB_INVALID_DURABLE_PAYLOAD",
                It.IsAny<string?>(),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_LegacyPayloadCannotTrustSchemaValidationMarker()
    {
        var payload = CreatePayload();
        payload.JobId = Guid.Empty;
        payload.TransitionSchemaValidated = true;
        var handler = CreateHandler();
        WorkflowExecutionContext? capturedContext = null;

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((context, _) => capturedContext = context)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Status = InstanceStatus.Active
            }));

        await handler.HandleAsync(payload, CancellationToken.None);

        Assert.NotNull(capturedContext);
        Assert.False(capturedContext.TransitionSchemaValidated);
    }

    /// <summary>
    /// Transient lock conflicts (producer accept lock / finishing chain still holds the
    /// execution lock) must be retried; success on a later attempt needs no recovery.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenLockConflictThenSuccess_RetriesWithoutRecovery()
    {
        var instanceId = Guid.NewGuid();
        var payload = CreatePayload(instanceId);
        var handler = CreateHandler();
        var calls = 0;

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls <= 2
                ? LockConflictResult()
                : Result<TransitionOutput>.Ok(new TransitionOutput { Status = InstanceStatus.Active }));

        await handler.HandleAsync(payload, CancellationToken.None);

        Assert.Equal(3, calls);
        _recoveryService.Verify(
            r => r.FaultInstanceAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _recoveryService.Verify(
            r => r.FaultInstanceAsync(
                It.IsAny<TransitionJobPayload>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(instanceId, payload.JobName, CancellationToken.None),
            Times.Once);
    }

    /// <summary>
    /// Persistent lock conflict → exactly MaxAttempts executions, then the instance is
    /// routed to recovery (Faulted) instead of being silently stranded in Busy.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenLockConflictExhausted_FaultsInstance()
    {
        var instanceId = Guid.NewGuid();
        var payload = CreatePayload(instanceId);
        payload.JobId = Guid.NewGuid();
        SetupDurableJob(payload);
        var handler = CreateHandler(
            lockConflictRetry: new LockConflictRetryOptions { MaxAttempts = 3, BaseDelayMilliseconds = 1 });

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockConflictResult);

        await handler.HandleAsync(payload, CancellationToken.None);

        _executionService.Verify(
            s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        _recoveryService.Verify(
            r => r.FaultInstanceAsync(
                It.Is<TransitionJobPayload>(canonical => canonical.JobId == payload.JobId),
                It.IsAny<string>(), "JOB_LOCK_CONFLICT", CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsFailedAsync(
                payload.JobId,
                It.IsAny<Guid>(),
                "JOB_LOCK_CONFLICT",
                It.IsAny<string?>(),
                CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Misconfigured retry options (negative base delay, attempts beyond the shift range)
    /// must not produce negative delays — Task.Delay would throw ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithMisconfiguredRetryOptions_DoesNotThrow()
    {
        var instanceId = Guid.NewGuid();
        var payload = CreatePayload(instanceId);
        var handler = CreateHandler(
            lockConflictRetry: new LockConflictRetryOptions { MaxAttempts = 40, BaseDelayMilliseconds = -100 });

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockConflictResult);

        await handler.HandleAsync(payload, CancellationToken.None);

        // Negative base delay clamps to 0 and the shift is capped, so all 40 attempts run
        // instantly instead of crashing the job pipeline.
        _executionService.Verify(
            s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Exactly(40));
        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(instanceId, payload.JobName, CancellationToken.None),
            Times.Once);
    }

    /// <summary>
    /// Non-conflict business failures must not be retried. Recovery still runs once because any
    /// non-success result is terminal for this delivery.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenNonConflictFailure_DoesNotRetry()
    {
        var payload = CreatePayload();
        var handler = CreateHandler();

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(
                Error.Validation("Transition:NotFound", "Transition not found")));

        await handler.HandleAsync(payload, CancellationToken.None);

        _executionService.Verify(
            s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _recoveryService.Verify(
            r => r.FaultInstanceAsync(
                It.IsAny<TransitionJobPayload>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Execution budget expiring during a retry backoff delay must route into the existing
    /// timeout recovery path — no unhandled exception.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenBudgetExpiresDuringRetryDelay_CallsRecovery()
    {
        var payload = CreatePayload();
        var handler = CreateHandler(
            timeoutSeconds: 1,
            lockConflictRetry: new LockConflictRetryOptions { MaxAttempts = 5, BaseDelayMilliseconds = 60_000 });

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockConflictResult);

        await handler.HandleAsync(payload, CancellationToken.None);

        _recoveryService.Verify(
            r => r.FaultInstanceAsync(payload, CancellationToken.None),
            Times.Once);
    }

    /// <summary>
    /// MarkAsProcessedAsync must always run — including after a timeout recovery.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AfterTimeout_AlwaysCallsMarkAsProcessed()
    {
        var instanceId = Guid.NewGuid();
        var payload = CreatePayload(instanceId);
        var handler = CreateHandler(timeoutSeconds: 0);

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns<WorkflowExecutionContext, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Result<TransitionOutput>.Ok(new TransitionOutput());
            });

        await handler.HandleAsync(payload, CancellationToken.None);

        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(instanceId, payload.JobName, CancellationToken.None),
            Times.Once);
    }

    /// <summary>
    /// Unhandled execution exceptions follow the same durable failure path as failure Results.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenUnhandledExceptionOccurs_RecoversAndMarksJobFailed()
    {
        var instanceId = Guid.NewGuid();
        var payload = CreatePayload(instanceId);
        payload.JobId = Guid.NewGuid();
        SetupDurableJob(payload);
        var handler = CreateHandler();

        _jobRepo
            .Setup(r => r.TryClaimAsync(
                payload.JobId, It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected infrastructure error"));

        await handler.HandleAsync(payload, CancellationToken.None);

        _jobRepo.Verify(
            r => r.MarkAsFailedAsync(
                payload.JobId,
                It.IsAny<Guid>(),
                "JOB_UNHANDLED_EXCEPTION",
                "unexpected infrastructure error",
                CancellationToken.None),
            Times.Once);
        _recoveryService.Verify(
            r => r.FaultInstanceAsync(
                It.Is<TransitionJobPayload>(canonical => canonical.JobId == payload.JobId),
                "unexpected infrastructure error",
                "JOB_UNHANDLED_EXCEPTION",
                CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
