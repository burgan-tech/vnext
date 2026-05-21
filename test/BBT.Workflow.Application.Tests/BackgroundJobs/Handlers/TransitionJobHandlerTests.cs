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
    }

    private TransitionJobHandler CreateHandler(int timeoutSeconds = 300)
    {
        var options = Options.Create(new WorkflowExecutionOptions
        {
            TransitionJobTimeoutSeconds = timeoutSeconds
        });
        return new TransitionJobHandler(
            _jobRepo.Object, _executionService.Object, _currentSchema.Object,
            _recoveryService.Object, options, _hostLifetime.Object, _logger.Object);
    }

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
