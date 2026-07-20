using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
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
    /// Host shutdown (SIGTERM) → do NOT call recovery; the host is going down.
    /// Simulated by cancelling both the outer token and ApplicationStopping.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenHostIsShuttingDown_DoesNotCallRecovery()
    {
        var payload = CreatePayload();
        var handler = CreateHandler(timeoutSeconds: 300);
        using var outerCts = new CancellationTokenSource();
        outerCts.Cancel();
        await _appStoppingCts.CancelAsync(); // Simulate SIGTERM

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
            r => r.FaultInstanceAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
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

    /// <summary>
    /// Pipeline returns a failure Result (e.g. validation error) → no recovery; handled inline.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenPipelineReturnsFailure_DoesNotCallRecovery()
    {
        var payload = CreatePayload();
        var handler = CreateHandler();

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(
                Error.Validation("Transition:NotFound", "Transition not found")));

        await handler.HandleAsync(payload, CancellationToken.None);

        _recoveryService.Verify(
            r => r.FaultInstanceAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
        var handler = CreateHandler(
            lockConflictRetry: new LockConflictRetryOptions { MaxAttempts = 3, BaseDelayMilliseconds = 1 });

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
                payload, It.IsAny<string>(), "JOB_LOCK_CONFLICT", CancellationToken.None),
            Times.Once);
        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(instanceId, payload.JobName, CancellationToken.None),
            Times.Once);
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
    /// Non-conflict business failures must not be retried (single execution, no recovery).
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
            Times.Never);
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
    /// MarkAsProcessedAsync must always run — even after an unhandled exception.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenUnhandledExceptionOccurs_AlwaysCallsMarkAsProcessed()
    {
        var instanceId = Guid.NewGuid();
        var payload = CreatePayload(instanceId);
        var handler = CreateHandler();

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected infrastructure error"));

        await handler.HandleAsync(payload, CancellationToken.None);

        _jobRepo.Verify(
            r => r.MarkAsProcessedAsync(instanceId, payload.JobName, CancellationToken.None),
            Times.Once);
        _recoveryService.Verify(
            r => r.FaultInstanceAsync(It.IsAny<TransitionJobPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
