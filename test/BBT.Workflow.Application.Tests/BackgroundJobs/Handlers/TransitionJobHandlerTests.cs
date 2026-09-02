using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Tracing;
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
    private readonly Mock<ICorrelationIdProvider> _correlationIdProvider = new();
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
            _recoveryService.Object, options, _hostLifetime.Object, _correlationIdProvider.Object, _logger.Object);
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
    /// The job span IS the transaction in APM, so it must name the transition it runs. Before this,
    /// every transition job showed up as the same "TransitionJob.Execute" transaction and the key
    /// was only reachable as a tag — which is also why a redundant <c>transition/{key}</c> child
    /// span existed underneath. Naming the lane span removes the need for that child.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NamesTheJobSpanAfterTheTransition()
    {
        var payload = CreatePayload();
        var handler = CreateHandler();
        var collected = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.BackgroundJobs",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput()));

        await handler.HandleAsync(payload, CancellationToken.None);

        Assert.Single(collected, a => a.DisplayName == "TransitionJob.Execute/go");
    }

    /// <summary>
    /// The activation episode carried by the payload must be the ambient episode while the pipeline
    /// runs, so the hop that brings the instance to rest measures from the originating request.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithPayloadEpisode_RestoresItForTheDurationOfTheJob()
    {
        var payload = CreatePayload();
        payload.EpisodeStartedAt = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
        payload.EpisodeTrigger = TelemetryConstants.ActivationTriggers.Manual;
        payload.EpisodeTransitionKey = "go";
        payload.EpisodeTraceRoot = "00-11111111111111111111111111111111-2222222222222222-01";
        var handler = CreateHandler();

        ActivationEpisode? observed = null;
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((_, _) => observed = WorkflowTraceLane.Episode)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput()));

        await handler.HandleAsync(payload, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(payload.EpisodeStartedAt, observed!.StartedAt);
        Assert.Equal(TelemetryConstants.ActivationTriggers.Manual, observed.Trigger);
        Assert.Equal("go", observed.TransitionKey);
        Assert.Equal(payload.EpisodeTraceRoot, observed.TraceRoot);
        Assert.False(observed.Partial);
        Assert.Null(WorkflowTraceLane.Episode);
    }

    /// <summary>
    /// A payload from a build that predates the episode must not inherit the Dapr callback
    /// request's episode, nor invent a start: the hop reports a partial episode covering itself.
    /// </summary>
    [Fact]
    public async Task HandleAsync_LegacyPayloadWithoutEpisode_SeedsAPartialEpisode()
    {
        var payload = CreatePayload();
        var handler = CreateHandler();

        ActivationEpisode? observed = null;
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((_, _) => observed = WorkflowTraceLane.Episode)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput()));

        // The callback request's own episode, which the job must NOT inherit.
        var callback = new ActivationEpisode(DateTimeOffset.UtcNow.AddHours(-1), "http", null, false);
        using (WorkflowTraceLane.Use("00-11111111111111111111111111111111-1111111111111111-01", episode: callback))
        {
            await handler.HandleAsync(payload, CancellationToken.None);
        }

        Assert.NotNull(observed);
        Assert.True(observed!.Partial);
        Assert.Equal(TelemetryConstants.ActivationTriggers.Job, observed.Trigger);
        Assert.Equal("go", observed.TransitionKey);
        Assert.NotEqual(callback.StartedAt, observed.StartedAt);
    }

    /// <summary>
    /// The captured x-request-id must be restored into the correlation provider for the duration
    /// of the job so downstream calls (Execution invoke, cross-domain) keep the client's request id.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithRequestIdHeader_RestoresCorrelationIdBeforeStartingTheJobSpan()
    {
        var payload = CreatePayload();
        payload.Headers["x-request-id"] = "req-abc-123";
        var handler = CreateHandler();
        var correlationChanged = false;
        var correlationWasChangedWhenActivityStarted = false;
        _correlationIdProvider
            .Setup(p => p.Change("req-abc-123"))
            .Callback(() => correlationChanged = true);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.BackgroundJobs",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.DisplayName == "TransitionJob.Execute/go")
                    correlationWasChangedWhenActivityStarted = correlationChanged;
            }
        };
        ActivitySource.AddActivityListener(listener);

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput()));

        await handler.HandleAsync(payload, CancellationToken.None);

        _correlationIdProvider.Verify(p => p.Change("req-abc-123"), Times.Once);
        Assert.True(correlationWasChangedWhenActivityStarted);
    }

    /// <summary>
    /// The payload's business correlation id must be seeded into the rebuilt execution context so
    /// the job continues the originating chain's correlation.id instead of minting a new one.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithPayloadCorrelationId_SeedsExecutionContext()
    {
        var payload = CreatePayload();
        payload.CorrelationId = "abc123def456abc123def456abc12345";
        var handler = CreateHandler();

        WorkflowExecutionContext? capturedContext = null;
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput()));

        await handler.HandleAsync(payload, CancellationToken.None);

        Assert.NotNull(capturedContext);
        Assert.Equal("abc123def456abc123def456abc12345", capturedContext!.CorrelationId);
    }

    /// <summary>
    /// Without an x-request-id header the provider must not be touched (no Change(null) noise).
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithoutRequestIdHeader_DoesNotChangeCorrelationId()
    {
        var payload = CreatePayload();
        var handler = CreateHandler();

        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput()));

        await handler.HandleAsync(payload, CancellationToken.None);

        _correlationIdProvider.Verify(p => p.Change(It.IsAny<string?>()), Times.Never);
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
