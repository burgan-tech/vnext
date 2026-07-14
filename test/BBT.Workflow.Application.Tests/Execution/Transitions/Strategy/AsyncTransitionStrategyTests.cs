using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Execution.Strategies;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Strategy;

/// <summary>
/// Unit tests for AsyncTransitionStrategy.
/// Tests asynchronous transition execution strategy via ITransitionEnqueueGateway.
/// </summary>
public class AsyncTransitionStrategyTests
{
    private readonly Mock<ITransitionContextFactory> _mockContextFactory = new();
    private readonly Mock<IInstanceJobRepository> _mockJobRepository = new();
    private readonly Mock<IDistributedLockService> _mockDistributedLockService = new();
    private readonly ReservedTransitionResolver _reservedTransitionResolver = new();
    private readonly Mock<ITransitionValidationService> _mockValidationService = new();
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<IInstanceBusyManager> _mockBusyManager = new();
    private readonly Mock<ITransitionEnqueueGateway> _mockEnqueueGateway = new();
    private readonly Mock<ILogger<AsyncTransitionStrategy>> _mockLogger = new();
    private readonly AsyncTransitionStrategy _strategy;

    public AsyncTransitionStrategyTests()
    {
        var innerUow = new Mock<IUnitOfWork>();
        innerUow.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        innerUow.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _uowManager
            .Setup(x => x.Begin(It.IsAny<UnitOfWorkOptions>()))
            .Returns(innerUow.Object);

        _mockValidationService
            .Setup(x => x.ValidateAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        _mockBusyManager
            .Setup(x => x.MarkBusyWithPropagationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockBusyManager
            .Setup(x => x.TryMarkBusyWithPropagationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BusyMarkOutcome.Marked);

        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockJobRepository
            .Setup(x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(It.IsAny<InstanceJob>());

        _mockDistributedLockService
            .Setup(x => x.ExecuteWithLockAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<Task>, int, CancellationToken>(async (_, action, _, _) =>
            {
                await action();
                return true;
            });

        _strategy = new AsyncTransitionStrategy(
            _mockContextFactory.Object,
            _mockJobRepository.Object,
            _mockDistributedLockService.Object,
            _reservedTransitionResolver,
            _mockValidationService.Object,
            _uowManager.Object,
            _mockBusyManager.Object,
            _mockEnqueueGateway.Object,
            _mockLogger.Object);
    }

    #region ExecuteAsync — happy path

    [Fact]
    public async Task ExecuteAsync_WithValidContext_ShouldEnqueueJobSuccessfully()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();

        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCallBusyManagerBeforeEnqueue()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        var busyManagerCalled = false;
        var enqueueCalledAfterBusy = false;

        _mockBusyManager
            .Setup(x => x.TryMarkBusyWithPropagationAsync(txCtx.Instance.Id, It.IsAny<CancellationToken>()))
            .Callback(() => busyManagerCalled = true)
            .ReturnsAsync(BusyMarkOutcome.Marked);

        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => enqueueCalledAfterBusy = busyManagerCalled)
            .Returns(Task.CompletedTask);

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        busyManagerCalled.ShouldBeTrue();
        enqueueCalledAfterBusy.ShouldBeTrue();
    }

    #endregion

    #region ExecuteAsync — failures

    [Fact]
    public async Task ExecuteAsync_WhenContextCreationFails_ShouldReturnFailure()
    {
        var wfCtx = CreateWorkflowExecutionContext();
        var error = Error.NotFound("instance.notfound", "Instance not found");

        _mockContextFactory
            .Setup(x => x.CreateAsync(wfCtx, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionExecutionContext>.Fail(error));

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(error);

        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSchemaValidationFails_ShouldReturnFailureWithoutEnqueue()
    {
        var (wfCtx, _) = SetupSuccessfulContext();
        var validationError = Error.Validation("schema.invalid", "Field 'amount' is required");

        _mockValidationService
            .Setup(x => x.ValidateAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail(validationError));

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(validationError);

        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        _mockDistributedLockService.Verify(
            x => x.ExecuteWithLockAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnqueueFails_ShouldPropagateException()
    {
        // Infrastructure exceptions from the gateway are not wrapped in Result —
        // they propagate to the global exception handler (HTTP middleware).
        var (wfCtx, _) = SetupSuccessfulContext();

        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Gateway unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await _strategy.ExecuteAsync(wfCtx, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellation_ShouldPropagateCancellation()
    {
        var wfCtx = CreateWorkflowExecutionContext();
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _mockContextFactory
            .Setup(x => x.CreateAsync(wfCtx, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _strategy.ExecuteAsync(wfCtx, cts.Token));
    }

    #endregion

    #region Busy manager delegation

    [Fact]
    public async Task ExecuteAsync_WithNormalTransition_ShouldTryMarkBusyViaManager()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        _mockBusyManager.Verify(
            x => x.TryMarkBusyWithPropagationAsync(txCtx.Instance.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockBusyManager.Verify(
            x => x.MarkBusyWithPropagationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithReservedTransition_ShouldMarkBusyUnconditionally()
    {
        // Reserved transitions (cancel/exit/...) are accepted while the instance is Busy by design,
        // so they must not go through the Try guard.
        var (wfCtx, txCtx) = SetupSuccessfulContext(transitionKey: "cancel");

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        _mockBusyManager.Verify(
            x => x.MarkBusyWithPropagationAsync(txCtx.Instance.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockBusyManager.Verify(
            x => x.TryMarkBusyWithPropagationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenIsInternalResume_ShouldSkipBusyManager()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        txCtx.Directives.MarkAsSubFlowResume();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        _mockBusyManager.Verify(
            x => x.MarkBusyWithPropagationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockBusyManager.Verify(
            x => x.TryMarkBusyWithPropagationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceAlreadyBusy_ShouldReturn409WithoutEnqueue()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();

        _mockBusyManager
            .Setup(x => x.TryMarkBusyWithPropagationAsync(txCtx.Instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BusyMarkOutcome.AlreadyBusy);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);

        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBusyMarkSkipped_ShouldStillEnqueue()
    {
        // Skipped = instance not found or completed — preserves the previous silent no-op
        // semantics; upstream instance resolution gates those states.
        var (wfCtx, txCtx) = SetupSuccessfulContext();

        _mockBusyManager
            .Setup(x => x.TryMarkBusyWithPropagationAsync(txCtx.Instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BusyMarkOutcome.Skipped);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Enqueue lock key scoping

    [Fact]
    public async Task ExecuteAsync_WithNormalTransition_ShouldLockOnEnqueueSuffixedKey()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        var capturedKey = CaptureLockKey();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        capturedKey().ShouldBe(txCtx.LockKey + AsyncTransitionStrategy.EnqueueLockSuffix);
        // Invariant: the producer must never lock the consumer's execution key —
        // the Dapr job fires while this lock is still held (race condition).
        capturedKey().ShouldNotBe(txCtx.LockKey);
    }

    [Fact]
    public async Task ExecuteAsync_WithReservedTransition_ShouldLockOnOwnTypeScopedEnqueueKey()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext(transitionKey: "cancel");
        var capturedKey = CaptureLockKey();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        _reservedTransitionResolver.IsReserved(txCtx).ShouldBeTrue();
        capturedKey().ShouldBe(txCtx.LockKey + ":cancel" + AsyncTransitionStrategy.EnqueueLockSuffix);
        // Invariant: never the consumer's reserved execution key either.
        capturedKey().ShouldNotBe(txCtx.LockKey + ":cancel");
        capturedKey().ShouldNotBe(txCtx.LockKey);
    }

    #endregion

    #region Helpers

    private Func<string?> CaptureLockKey()
    {
        string? captured = null;
        _mockDistributedLockService
            .Setup(x => x.ExecuteWithLockAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<Task>, int, CancellationToken>(async (key, action, _, _) =>
            {
                captured = key;
                await action();
                return true;
            });
        return () => captured;
    }

    private (WorkflowExecutionContext, TransitionExecutionContext) SetupSuccessfulContext(string transitionKey = "test-transition")
    {
        var wfCtx = CreateWorkflowExecutionContext(transitionKey);
        var txCtx = CreateTransitionExecutionContext(transitionKey);
        wfCtx.InstanceId = txCtx.InstanceId.ToString();

        _mockContextFactory
            .Setup(x => x.CreateAsync(wfCtx, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionExecutionContext>.Ok(txCtx));

        return (wfCtx, txCtx);
    }

    private static WorkflowExecutionContext CreateWorkflowExecutionContext(string transitionKey = "test-transition") =>
        new()
        {
            InstanceId = Guid.NewGuid().ToString(),
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = transitionKey,
            TriggerType = TriggerType.Manual,
            Actor = ExecutionActor.User,
            Headers = new Dictionary<string, string?>(),
            RouteValues = new Dictionary<string, string?>()
        };

    private static TransitionExecutionContext CreateTransitionExecutionContext(string transitionKey = "test-transition")
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create(transitionKey, null, "state1", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = transition,
            Instance = instance,
            Data = new { test = "data" },
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateMockWorkflow(string key, string domain)
    {
        const string json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [
                {
                    "key": "state1",
                    "type": "P",
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }

    #endregion
}
