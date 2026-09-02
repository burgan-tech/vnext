using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using BBT.Aether.BackgroundJob;
using BBT.Workflow.BackgroundJobs;
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
    private readonly Mock<ITransitionValidationService> _mockValidationService = new();
    private readonly Mock<IUnitOfWorkManager> _uowManager = new();
    private readonly Mock<ITransitionAdmissionService> _mockAdmissionService = new();
    private readonly Mock<ITransitionEnqueueGateway> _mockEnqueueGateway = new();
    private readonly Mock<IBackgroundJobArmHandle> _mockArmHandle = new();

    /// <summary>True while the faked admission holds its status lock (i.e. during the callback).</summary>
    private bool _lockHeld;
    private readonly Mock<ILogger<AsyncTransitionStrategy>> _mockLogger = new();
    private readonly AsyncTransitionStrategy _strategy;

    /// <summary>
    /// Flip the faked admission reports to the callback. Admission owns the kind→flip policy now
    /// (pinned in TransitionAdmissionServiceTests); these tests drive it as an input.
    /// </summary>
    private AcceptFlip _acceptFlip = AcceptFlip.Reserved;

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

        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TransitionEnqueueOutcome(TransitionEnqueuePath.Direct, _mockArmHandle.Object));

        _mockJobRepository
            .Setup(x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(It.IsAny<InstanceJob>());

        // Default: admission admits and runs the callback under its lock, reporting _acceptFlip.
        SetupAdmissionAdmits();

        _strategy = new AsyncTransitionStrategy(
            _mockContextFactory.Object,
            _mockJobRepository.Object,
            _mockValidationService.Object,
            _uowManager.Object,
            _mockAdmissionService.Object,
            _mockEnqueueGateway.Object,
            _mockLogger.Object);
    }

    #region ExecuteAsync — happy path

    /// <summary>
    /// The accept carries the activation episode onto BOTH delivery shapes (the gateway may fall
    /// back from the direct payload to the outbox event), so the job that finally brings the instance
    /// to rest measures from this request's arrival rather than from its own start.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShouldCarryTheActivationEpisodeOntoPayloadAndOutboxEvent()
    {
        var (wfCtx, _) = SetupSuccessfulContext();
        var (payload, outboxEvent) = CaptureEnqueue();
        var episode = new ActivationEpisode(
            new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
            TelemetryConstants.ActivationTriggers.Manual,
            "test-transition",
            Partial: false,
            TraceRoot: "00-11111111111111111111111111111111-2222222222222222-01");

        using (WorkflowTraceLane.Use("00-11111111111111111111111111111111-1111111111111111-01", episode: episode))
        {
            var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);
            result.IsSuccess.ShouldBeTrue();
        }

        payload().ShouldNotBeNull().EpisodeStartedAt.ShouldBe(episode.StartedAt);
        payload()!.EpisodeTrigger.ShouldBe(TelemetryConstants.ActivationTriggers.Manual);
        payload()!.EpisodeTransitionKey.ShouldBe("test-transition");
        payload()!.EpisodeTraceRoot.ShouldBe(episode.TraceRoot);
        outboxEvent().ShouldNotBeNull().EpisodeStartedAt.ShouldBe(episode.StartedAt);
        outboxEvent()!.EpisodeTrigger.ShouldBe(TelemetryConstants.ActivationTriggers.Manual);
        outboxEvent()!.EpisodeTransitionKey.ShouldBe("test-transition");
        outboxEvent()!.EpisodeTraceRoot.ShouldBe(episode.TraceRoot);
    }

    /// <summary>
    /// The accept's durable half (job row + delivery decision + commit) and the Dapr scheduler arm
    /// used to be the unnamed tail of the server span; both are spans now.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShouldSpanTheEnqueueAndTheArm()
    {
        // A root span scopes the process-wide listener to THIS test's trace.
        using var root = new System.Diagnostics.Activity("test-root");
        root.SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C);
        root.Start();
        var collected = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name is "BBT.Workflow.Pipeline" or "BBT.Workflow.BackgroundJobs",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.TraceId != root.TraceId) return;
                lock (collected) collected.Add(a);
            }
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var (wfCtx, _) = SetupSuccessfulContext();

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);
        System.Diagnostics.Activity.Current = null;

        result.IsSuccess.ShouldBeTrue();
        var enqueue = collected.Single(a => a.DisplayName == "Transition.Enqueue");
        enqueue.GetTagItem(TelemetryConstants.TagNames.EnqueuePath).ShouldBe("Direct");
        enqueue.GetTagItem(TelemetryConstants.TagNames.JobName).ShouldNotBeNull();
        var arm = collected.Single(a => a.DisplayName == "BackgroundJob.Arm");
        arm.GetTagItem(TelemetryConstants.TagNames.SpanCategory).ShouldBe(TelemetryConstants.SpanCategories.Business);
        _mockArmHandle.Verify(h => h.ArmAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

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
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldEnqueueInsideTheAdmissionLock()
    {
        // The status flip happens first and the job is persisted while admission still holds the
        // lock — so the accept answers a caller that already reads Busy.
        var (wfCtx, _) = SetupSuccessfulContext();
        var admitted = false;
        var enqueuedAfterAdmission = false;

        _mockAdmissionService
            .Setup(x => x.AcceptAsync(
                It.IsAny<TransitionExecutionContext>(),
                It.IsAny<Func<AcceptFlip, CancellationToken, Task<Result>>>(),
                It.IsAny<CancellationToken>()))
            .Returns<TransitionExecutionContext, Func<AcceptFlip, CancellationToken, Task<Result>>, CancellationToken>(
                (_, underLock, ct) =>
                {
                    admitted = true;
                    return underLock(_acceptFlip, ct);
                });

        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => enqueuedAfterAdmission = admitted)
            .ReturnsAsync(() => new TransitionEnqueueOutcome(TransitionEnqueuePath.Direct, _mockArmHandle.Object));

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        admitted.ShouldBeTrue();
        enqueuedAfterAdmission.ShouldBeTrue();
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
                It.IsAny<bool>(),
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
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Validation runs BEFORE any lock is taken — admission is never even reached.
        _mockAdmissionService.Verify(
            x => x.AcceptAsync(
                It.IsAny<TransitionExecutionContext>(),
                It.IsAny<Func<AcceptFlip, CancellationToken, Task<Result>>>(),
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
                It.IsAny<bool>(),
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

    #region Admission delegation

    [Fact]
    public async Task ExecuteAsync_ShouldAdmitThroughTheSingleAdmissionLock()
    {
        // The accept owns no lock of its own any more: admission takes ctx.LockKey once and the
        // enqueue runs inside that critical section.
        var (wfCtx, _) = SetupSuccessfulContext();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        _mockAdmissionService.Verify(
            x => x.AcceptAsync(
                It.IsAny<TransitionExecutionContext>(),
                It.IsAny<Func<AcceptFlip, CancellationToken, Task<Result>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotPerformItsOwnStatusFlips()
    {
        // Kind→flip policy belongs to admission; the strategy must not reach for the individual
        // reserve/takeover entry points (they would acquire the same key a second time).
        var (wfCtx, _) = SetupSuccessfulContext();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        _mockAdmissionService.Verify(
            x => x.ReserveAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockAdmissionService.Verify(
            x => x.TakeOverAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockAdmissionService.Verify(
            x => x.ReserveSubflowChainAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockAdmissionService.Verify(
            x => x.ReleaseReservationAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockAdmissionService.Verify(
            x => x.ReleaseSubflowChainAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAdmissionRejects_ShouldNotEnqueue()
    {
        // Busy 409, lock conflict, completed instance — admission decides and the callback that
        // would persist the job never runs.
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        SetupAdmissionRejects(WorkflowErrors.InstanceBusy(txCtx.Instance.Id, txCtx.TransitionKey));

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAnActiveJobAlreadyExists_ShouldRejectInsideTheLock()
    {
        // The duplicate-request guard shares admission's critical section — its check-then-insert
        // has no database constraint behind it.
        var (wfCtx, _) = SetupSuccessfulContext();
        _mockJobRepository
            .Setup(x => x.AnyActiveTransitionJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<JobType>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TransitionLocked);
        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateData_ShouldSkipTheDuplicateJobGuard_AndAcceptInParallel()
    {
        // updateData must accept EVERY request: two parallel accepts share the same logical job
        // identity (instance, source state, transition key) yet are both legitimate — each carries
        // its own payload, and deduping one would lose a caller's data. Physical collision is
        // impossible (job id/name are unique per enqueue), so the guard is not even consulted.
        var (wfCtx, _) = SetupSuccessfulContext();
        _acceptFlip = AcceptFlip.None;
        _mockAdmissionService
            .Setup(x => x.Classify(It.IsAny<TransitionExecutionContext>()))
            .Returns(AdmissionKind.Unconditional);
        _mockJobRepository
            .Setup(x => x.AnyActiveTransitionJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<JobType>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // an active twin exists — must NOT reject this accept

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _mockJobRepository.Verify(
            x => x.AnyActiveTransitionJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<JobType>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Subflow chain claim

    [Fact]
    public async Task ExecuteAsync_WhenAdmissionReservedTheChain_ShouldStampTheClaimOnTheEnqueuedJob()
    {
        // Without the claim the leaf — which this accept flipped Busy — rejects the relay with 409.
        var (wfCtx, _) = SetupSuccessfulContext();
        _acceptFlip = AcceptFlip.ChainReserved;

        var (payload, outboxEvent) = CaptureEnqueue();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        payload()!.SubflowChainReserved.ShouldBeTrue();
        outboxEvent()!.SubflowChainReserved.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenAdmissionTookNoChainReserve_ShouldNotStampTheClaim()
    {
        // A forward may never claim a reserve that was not taken, or it would barge past a leaf
        // that is Busy for its own reasons.
        var (wfCtx, _) = SetupSuccessfulContext();
        _acceptFlip = AcceptFlip.Reserved;

        var (payload, _) = CaptureEnqueue();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        payload()!.SubflowChainReserved.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenRelayArrivesWithAnInheritedClaim_ShouldCarryItOntoTheNextHop()
    {
        // An intermediate relay's own accept classifies as OwnerReentry, so admission performs no
        // flip for it. If the claim were not inherited from the context it would be dropped after
        // the first hop and the leaf would reject the forward with a 409, deadlocking the chain.
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        txCtx.SubflowChainReserved = true;
        _acceptFlip = AcceptFlip.None;

        var (payload, _) = CaptureEnqueue();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        payload()!.SubflowChainReserved.ShouldBeTrue();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Fakes admission's single-lock accept: runs the callback (the duplicate guard and the
    /// enqueue) and reports <see cref="_acceptFlip"/> to it, the way the real service does while
    /// holding ctx.LockKey.
    /// </summary>
    private void SetupAdmissionAdmits()
    {
        _mockAdmissionService
            .Setup(x => x.AcceptAsync(
                It.IsAny<TransitionExecutionContext>(),
                It.IsAny<Func<AcceptFlip, CancellationToken, Task<Result>>>(),
                It.IsAny<CancellationToken>()))
            .Returns<TransitionExecutionContext, Func<AcceptFlip, CancellationToken, Task<Result>>, CancellationToken>(
                async (_, underLock, ct) =>
                {
                    // Mirror the real lock lifetime: held for the callback only. Lets a test observe
                    // whether work happened inside the critical section or after it.
                    _lockHeld = true;
                    try
                    {
                        return await underLock(_acceptFlip, ct);
                    }
                    finally
                    {
                        _lockHeld = false;
                    }
                });
    }

    /// <summary>
    /// Fakes a rejected accept: the callback never runs, exactly as when the status flip fails.
    /// </summary>
    private void SetupAdmissionRejects(Error error)
    {
        _mockAdmissionService
            .Setup(x => x.AcceptAsync(
                It.IsAny<TransitionExecutionContext>(),
                It.IsAny<Func<AcceptFlip, CancellationToken, Task<Result>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail(error));
    }

    private (Func<TransitionJobPayload?>, Func<TransitionContinuationRequested?>) CaptureEnqueue()
    {
        TransitionJobPayload? payload = null;
        TransitionContinuationRequested? outboxEvent = null;

        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransitionJobPayload, TransitionContinuationRequested, bool, CancellationToken>(
                (p, e, _, _) => { payload = p; outboxEvent = e; })
            .ReturnsAsync(() => new TransitionEnqueueOutcome(TransitionEnqueuePath.Direct, _mockArmHandle.Object));

        return (() => payload, () => outboxEvent);
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

    [Fact]
    public async Task Accept_ShouldDeferArmingSoTheSchedulerIsNotCalledUnderTheStatusLock()
    {
        // The point of the change: the durable row commits under the lock (the duplicate-job guard
        // needs the next reader to see it) but the Dapr round-trip does not. Measured, that call WAS
        // the lock hold under load, serializing every other request on the same instance behind it.
        var (wfCtx, _) = SetupSuccessfulContext();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Accept_ShouldArmAfterTheLockIsReleased_OnTheDirectPath()
    {
        var (wfCtx, _) = SetupSuccessfulContext();

        var armedWhileLockHeld = false;
        _mockArmHandle
            .Setup(x => x.ArmAsync(It.IsAny<CancellationToken>()))
            .Callback(() => armedWhileLockHeld = _lockHeld)
            .Returns(Task.CompletedTask);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _mockArmHandle.Verify(x => x.ArmAsync(It.IsAny<CancellationToken>()), Times.Once);
        armedWhileLockHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task Accept_ShouldNotArm_WhenTheGatewayFellBackToTheOutbox()
    {
        // The outbox relay owns delivery AND arming for its own job; arming here would either find
        // no row or race the relay.
        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransitionEnqueueOutcome(TransitionEnqueuePath.Outbox));

        var (wfCtx, _) = SetupSuccessfulContext();

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        _mockArmHandle.Verify(x => x.ArmAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
