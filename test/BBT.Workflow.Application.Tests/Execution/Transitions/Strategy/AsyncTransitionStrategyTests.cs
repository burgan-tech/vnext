using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Strategies;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
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
    private readonly Mock<IInstanceRepository> _mockInstanceRepository = new();
    private readonly Mock<ITransitionEnqueueGateway> _mockEnqueueGateway = new();
    private readonly Mock<IRequestRawBodyProvider> _mockRawBodyProvider = new();
    private readonly Mock<ITransitionCommitLeaseManager> _mockCommitLeaseManager = new();
    private readonly Mock<IDistributedLockHandle> _mockLockHandle = new();
    private readonly Mock<ILogger<AsyncTransitionStrategy>> _mockLogger = new();
    private readonly AsyncTransitionStrategy _strategy;

    public AsyncTransitionStrategyTests()
    {
        _mockValidationService
            .Setup(x => x.ValidateInputSchemaAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());
        _mockValidationService
            .Setup(x => x.ValidatePolicyAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        _mockRawBodyProvider.Setup(x => x.GetRawBody()).Returns("{\"amount\":42}");

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
            .Setup(x => x.TryAcquireLockAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockLockHandle.Object);

        _strategy = new AsyncTransitionStrategy(
            _mockContextFactory.Object,
            _mockJobRepository.Object,
            _mockDistributedLockService.Object,
            _reservedTransitionResolver,
            _mockValidationService.Object,
            _mockInstanceRepository.Object,
            _mockEnqueueGateway.Object,
            _mockRawBodyProvider.Object,
            _mockCommitLeaseManager.Object,
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
    public async Task ExecuteAsync_WhenSchemaWasValidatedAtBoundary_ShouldRecheckOnlyPolicy()
    {
        var (wfCtx, _) = SetupSuccessfulContext();
        wfCtx.TransitionSchemaValidated = true;
        _mockValidationService
            .Setup(x => x.ValidatePolicyAsync(
                It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _mockValidationService.Verify(
            x => x.ValidatePolicyAsync(
                It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockValidationService.Verify(
            x => x.ValidateInputSchemaAsync(
                It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPersistBusyReservationBeforeEnqueue()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        var reservationPersisted = false;
        var enqueueCalledAfterReservation = false;

        _mockInstanceRepository
            .Setup(x => x.UpdateAsync(txCtx.Instance, false, It.IsAny<CancellationToken>()))
            .Callback(() => reservationPersisted =
                txCtx.Instance.IsBusy && txCtx.Instance.ChainToken.HasValue)
            .ReturnsAsync(txCtx.Instance);

        _mockEnqueueGateway
            .Setup(x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => enqueueCalledAfterReservation = reservationPersisted)
            .Returns(Task.CompletedTask);

        await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        reservationPersisted.ShouldBeTrue();
        enqueueCalledAfterReservation.ShouldBeTrue();
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
            .Setup(x => x.ValidateInputSchemaAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
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
            x => x.TryAcquireLockAsync(
                It.IsAny<string>(),
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

    #region Atomic admission

    [Fact]
    public async Task ExecuteAsync_WithNormalTransition_ShouldReserveAndPersistDurableJob()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        InstanceJob? insertedJob = null;
        _mockJobRepository
            .Setup(x => x.InsertAsync(It.IsAny<InstanceJob>(), false, It.IsAny<CancellationToken>()))
            .Callback<InstanceJob, bool, CancellationToken>((job, _, _) => insertedJob = job)
            .ReturnsAsync((InstanceJob job, bool _, CancellationToken _) => job);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        txCtx.Instance.IsBusy.ShouldBeTrue();
        txCtx.Instance.ChainToken.ShouldNotBeNull();
        txCtx.ExpectedRevision.ShouldBe(txCtx.Instance.Revision + 1);
        insertedJob.ShouldNotBeNull();
        insertedJob!.AdmissionToken.ShouldBe(txCtx.Instance.ChainToken);
        insertedJob.AdmittedRevision.ShouldBe(txCtx.ExpectedRevision);
        insertedJob.Payload.ShouldNotBeNullOrWhiteSpace();
        _mockInstanceRepository.Verify(
            x => x.UpdateAsync(txCtx.Instance, false, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockInstanceRepository.Verify(
            x => x.GetResultAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithReservedTransition_ShouldReuseExistingBusyChain()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext(transitionKey: "cancel");
        var existingToken = Guid.NewGuid();
        txCtx.Instance.BeginChain(existingToken);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        txCtx.ChainToken.ShouldBe(existingToken);
        _mockInstanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenIsInternalResume_ShouldPreserveChainWithoutSecondInstanceWrite()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        var existingToken = Guid.NewGuid();
        txCtx.Instance.BeginChain(existingToken);
        txCtx.ChainToken = existingToken;
        txCtx.Directives.MarkAsSubFlowResume();

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        txCtx.ChainToken.ShouldBe(existingToken);
        _mockInstanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceAlreadyBusy_ShouldReturn409WithoutEnqueue()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        txCtx.Instance.BeginChain(Guid.NewGuid());

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _mockJobRepository.Verify(
            x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceBecomesBusyWhileWaitingForLock_ShouldRejectAuthoritativeSnapshot()
    {
        var (wfCtx, preflightContext) = SetupSuccessfulContext();
        var authoritativeContext = CreateTransitionExecutionContext(
            preflightContext.TransitionKey,
            preflightContext.InstanceId);
        authoritativeContext.Instance.BeginChain(Guid.NewGuid());

        _mockInstanceRepository
            .Setup(x => x.ReloadActiveAsync(wfCtx.InstanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Instance>.Ok(authoritativeContext.Instance));
        _mockContextFactory
            .Setup(x => x.CreateFromPreloaded(
                wfCtx,
                preflightContext.Workflow,
                authoritativeContext.Instance))
            .Returns(Result<TransitionExecutionContext>.Ok(authoritativeContext));

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
        preflightContext.Instance.IsBusy.ShouldBeFalse();
        _mockInstanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("S")]
    [InlineData("P")]
    public async Task ExecuteAsync_WithPreparedBusySubItemStart_ShouldAdoptChainAndEnqueue(string flowType)
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext(transitionKey: "start");
        txCtx.Instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = flowType;
        txCtx.Instance.Busy();

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        txCtx.Instance.IsBusy.ShouldBeTrue();
        txCtx.Instance.ChainToken.ShouldNotBeNull();
        txCtx.ExpectedRevision.ShouldBe(1);
        _mockInstanceRepository.Verify(
            x => x.UpdateAsync(txCtx.Instance, false, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockJobRepository.Verify(
            x => x.InsertAsync(It.IsAny<InstanceJob>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithBusySubItemStartThatAlreadyOwnsChain_ShouldRejectDuplicate()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext(transitionKey: "start");
        txCtx.Instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = "S";
        txCtx.Instance.BeginChain(Guid.NewGuid());

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
        _mockJobRepository.Verify(
            x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBusyParentHasActiveSubflow_ShouldReuseChainAndEnqueueForwardHop()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext(transitionKey: "child-go");
        txCtx.Instance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            txCtx.InstanceId,
            txCtx.Current.Key,
            Guid.NewGuid(),
            SubFlowType.SubFlow.Code,
            "child-domain",
            "child-flow",
            "1.0.0"));
        var chainToken = Guid.NewGuid();
        txCtx.Instance.BeginChain(chainToken);
        InstanceJob? insertedJob = null;
        _mockJobRepository
            .Setup(x => x.InsertAsync(It.IsAny<InstanceJob>(), false, It.IsAny<CancellationToken>()))
            .Callback<InstanceJob, bool, CancellationToken>((job, _, _) => insertedJob = job)
            .ReturnsAsync((InstanceJob job, bool _, CancellationToken _) => job);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        txCtx.ChainToken.ShouldBe(chainToken);
        txCtx.ExpectedRevision.ShouldBe(txCtx.Instance.Revision);
        insertedJob.ShouldNotBeNull();
        insertedJob!.AdmissionToken.ShouldBe(chainToken);
        insertedJob.AdmittedRevision.ShouldBe(txCtx.Instance.Revision);
        _mockInstanceRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Instance>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAdmissionSnapshotRevisionChanged_ShouldUseLockedAuthoritativeSnapshot()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        wfCtx.ExpectedRevision = txCtx.Instance.Revision + 1;

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithSameIdempotencyKeyAndFingerprint_ShouldReturnExistingAdmission()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        wfCtx.Headers["Idempotency-Key"] = "request-42";
        const string rawBody = "{\"amount\":42}";
        var fingerprint = AsyncTransitionStrategy.CreateRequestFingerprint(wfCtx, rawBody);
        var existingJob = InstanceJob.CreateTransitionAdmission(
            Guid.NewGuid(),
            JobName.ForAsyncTransition(txCtx.InstanceId, "state1", txCtx.TransitionKey),
            Guid.NewGuid(),
            wfCtx.Domain,
            wfCtx.WorkflowKey,
            txCtx.InstanceId,
            "{}",
            Guid.NewGuid(),
            7,
            "request-42",
            fingerprint);
        _mockJobRepository
            .Setup(x => x.FindByIdempotencyKeyAsReadOnlyAsync(
                txCtx.InstanceId, "request-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        txCtx.ExpectedRevision.ShouldBe(7);
        _mockJobRepository.Verify(
            x => x.InsertAsync(It.IsAny<InstanceJob>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenIdempotencyKeyWasUsedForDifferentBody_ShouldReturnConflict()
    {
        var (wfCtx, txCtx) = SetupSuccessfulContext();
        wfCtx.Headers["idempotency-key"] = "request-42";
        var existingJob = InstanceJob.CreateTransitionAdmission(
            Guid.NewGuid(),
            JobName.ForAsyncTransition(txCtx.InstanceId, "state1", txCtx.TransitionKey),
            Guid.NewGuid(),
            wfCtx.Domain,
            wfCtx.WorkflowKey,
            txCtx.InstanceId,
            "{}",
            Guid.NewGuid(),
            7,
            "request-42",
            new string('A', InstanceJobConstants.MaxRequestFingerprintLength));
        _mockJobRepository
            .Setup(x => x.FindByIdempotencyKeyAsReadOnlyAsync(
                txCtx.InstanceId, "request-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.IdempotencyKeyConflict);
        _mockEnqueueGateway.Verify(
            x => x.EnqueueAsync(
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<TransitionContinuationRequested>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenIdempotencyKeyIsTooLong_ShouldFailBeforeDatabaseAdmission()
    {
        var (wfCtx, _) = SetupSuccessfulContext();
        wfCtx.Headers["idempotency-key"] = new string('x', InstanceJobConstants.MaxIdempotencyKeyLength + 1);

        var result = await _strategy.ExecuteAsync(wfCtx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InvalidIdempotencyKey);
        _mockInstanceRepository.Verify(
            x => x.GetResultAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
            .Setup(x => x.TryAcquireLockAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, int, CancellationToken>((key, _, _) =>
            {
                captured = key;
                return Task.FromResult<IDistributedLockHandle?>(_mockLockHandle.Object);
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
        _mockContextFactory
            .Setup(x => x.CreateFromPreloaded(
                wfCtx,
                txCtx.Workflow,
                It.IsAny<Instance>()))
            .Returns(Result<TransitionExecutionContext>.Ok(txCtx));
        _mockInstanceRepository
            .Setup(x => x.ReloadActiveAsync(
                wfCtx.InstanceId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Instance>.Ok(txCtx.Instance));
        _mockInstanceRepository
            .Setup(x => x.UpdateAsync(
                It.IsAny<Instance>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance instance, bool _, CancellationToken _) => instance);

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

    private static TransitionExecutionContext CreateTransitionExecutionContext(
        string transitionKey = "test-transition",
        Guid? instanceId = null)
    {
        var resolvedInstanceId = instanceId ?? Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(resolvedInstanceId, workflowKey, "1.0.0");
        var state = workflow.GetState("state1").Value!;
        var transition = string.Equals(transitionKey, workflow.StartTransition.Key, StringComparison.Ordinal)
            ? workflow.StartTransition
            : Transition.Create(transitionKey, null, "state1", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = resolvedInstanceId,
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
