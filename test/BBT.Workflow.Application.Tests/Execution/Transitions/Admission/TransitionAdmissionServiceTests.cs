using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Admission;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Admission;

/// <summary>
/// Unit tests for <see cref="TransitionAdmissionService"/> — the Busy-as-mutex admission gate:
/// classification matrix, cheap Busy pre-check, reserve under the short status lock, and
/// reservation release compensation.
/// </summary>
public class TransitionAdmissionServiceTests
{
    private readonly IInstanceStatusLock _statusLock = Substitute.For<IInstanceStatusLock>();
    private readonly IInstanceBusyManager _busyManager = Substitute.For<IInstanceBusyManager>();
    private readonly ILogger<TransitionAdmissionService> _logger =
        Substitute.For<ILogger<TransitionAdmissionService>>();

    private TransitionAdmissionService CreateService()
        => new(_statusLock, _busyManager, _logger);

    private void SetupAcquiredLock()
    {
        var scope = Substitute.For<ITransitionLockScope>();
        scope.IsAcquired.Returns(true);
        _statusLock.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(scope);
    }

    private void SetupFailedLock()
    {
        var scope = Substitute.For<ITransitionLockScope>();
        scope.IsAcquired.Returns(false);
        _statusLock.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(scope);
    }

    #region Classification matrix

    [Theory]
    [InlineData("cancel", AdmissionKind.BypassBusyCheck)]
    [InlineData("exit", AdmissionKind.BypassBusyCheck)]
    [InlineData("update-parent-data", AdmissionKind.Unconditional)]
    [InlineData("regular-transition", AdmissionKind.Normal)]
    public void Classify_ByTransitionKey_ReturnsExpectedKind(string transitionKey, AdmissionKind expected)
    {
        var context = CreateContext(transitionKey);

        CreateService().Classify(context).ShouldBe(expected);
    }

    [Fact]
    public void Classify_TimeoutDirective_ReturnsBypassBusyCheck()
    {
        var context = CreateContext();
        context.Directives.MarkAsTimeoutTransition();

        CreateService().Classify(context).ShouldBe(AdmissionKind.BypassBusyCheck);
    }

    [Fact]
    public void Classify_SubFlowResume_ReturnsOwnerReentry()
    {
        var context = CreateContext();
        context.Directives.MarkAsSubFlowResume();

        CreateService().Classify(context).ShouldBe(AdmissionKind.OwnerReentry);
    }

    [Fact]
    public void Classify_LongPollAckResume_ReturnsOwnerReentry()
    {
        var context = CreateContext();
        context.Directives.MarkAsLongPollAckResume();

        CreateService().Classify(context).ShouldBe(AdmissionKind.OwnerReentry);
    }

    [Fact]
    public void Classify_PreReserved_ReturnsOwnerReentry()
    {
        // A background-job re-entry / per-job continuation: the accept already reserved Busy.
        var context = CreateContext();
        context.IsPreReserved = true;

        CreateService().Classify(context).ShouldBe(AdmissionKind.OwnerReentry);
    }

    [Fact]
    public void Classify_ErrorBoundaryTransition_ReturnsOwnerReentry()
    {
        var context = CreateContext(isErrorBoundaryTransition: true);

        CreateService().Classify(context).ShouldBe(AdmissionKind.OwnerReentry);
    }

    #endregion

    #region CheckAdmission

    [Fact]
    public void CheckAdmission_NormalOnBusyInstance_FailsWithInstanceBusy()
    {
        var context = CreateContext();
        context.Instance.Busy();

        var result = CreateService().CheckAdmission(context);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
    }

    [Fact]
    public void CheckAdmission_NormalOnActiveInstance_Succeeds()
    {
        var context = CreateContext();

        CreateService().CheckAdmission(context).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void CheckAdmission_PreReservedOnBusyInstance_Succeeds()
    {
        var context = CreateContext();
        context.Instance.Busy();
        context.IsPreReserved = true;

        CreateService().CheckAdmission(context).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void CheckAdmission_ErrorBoundaryTransitionOnBusyInstance_Succeeds()
    {
        var context = CreateContext(isErrorBoundaryTransition: true);
        context.Instance.Busy();

        CreateService().CheckAdmission(context).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("exit")]
    [InlineData("update-parent-data")]
    public void CheckAdmission_ExemptKindsOnBusyInstance_Succeed(string transitionKey)
    {
        var context = CreateContext(transitionKey);
        context.Instance.Busy();

        CreateService().CheckAdmission(context).IsSuccess.ShouldBeTrue();
    }

    #endregion

    #region ReserveAsync

    [Fact]
    public async Task ReserveAsync_WhenMarked_Succeeds()
    {
        var context = CreateContext();
        SetupAcquiredLock();
        _busyManager
            .TryMarkBusyWithPropagationAsync(context.InstanceId, Arg.Any<CancellationToken>())
            .Returns(BusyMarkOutcome.Marked);

        var result = await CreateService().ReserveAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ReserveAsync_WhenAlreadyBusy_FailsWithInstanceBusy()
    {
        // The authoritative re-check under the lock: a competitor won the reserve race.
        var context = CreateContext();
        SetupAcquiredLock();
        _busyManager
            .TryMarkBusyWithPropagationAsync(context.InstanceId, Arg.Any<CancellationToken>())
            .Returns(BusyMarkOutcome.AlreadyBusy);

        var result = await CreateService().ReserveAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
    }

    [Fact]
    public async Task ReserveAsync_WhenLockNotAcquired_FailsWithLockConflict()
    {
        var context = CreateContext();
        SetupFailedLock();

        var result = await CreateService().ReserveAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConflictWorkflow);
        await _busyManager.DidNotReceive()
            .TryMarkBusyWithPropagationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReserveAsync_WhenSkipped_FailsWithAlreadyCompleted()
    {
        var context = CreateContext();
        SetupAcquiredLock();
        _busyManager
            .TryMarkBusyWithPropagationAsync(context.InstanceId, Arg.Any<CancellationToken>())
            .Returns(BusyMarkOutcome.Skipped);

        var result = await CreateService().ReserveAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
    }

    #endregion

    #region TakeOverAsync

    [Fact]
    public async Task TakeOverAsync_AcquiresLockAndMarksBusy()
    {
        // Cancel/exit/timeout skip the Busy 409 but the flip still goes through the short lock.
        var context = CreateContext("cancel");
        SetupAcquiredLock();

        var result = await CreateService().TakeOverAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _statusLock.Received(1)
            .AcquireAsync(context.LockKey, Arg.Any<CancellationToken>());
        await _busyManager.Received(1)
            .MarkBusyAsync(context.InstanceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TakeOverAsync_WhenLockNotAcquired_FailsWithLockConflict()
    {
        var context = CreateContext("cancel");
        SetupFailedLock();

        var result = await CreateService().TakeOverAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConflictWorkflow);
        await _busyManager.DidNotReceive()
            .MarkBusyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region ClassifyKey (instance'sız sınıflandırma)

    [Theory]
    [InlineData("cancel", AdmissionKind.BypassBusyCheck)]
    [InlineData("EXIT", AdmissionKind.BypassBusyCheck)]
    [InlineData("update-parent-data", AdmissionKind.Unconditional)]
    [InlineData("regular-transition", AdmissionKind.Normal)]
    public void ClassifyKey_ByAlias_ReturnsExpectedKind(string transitionKey, AdmissionKind expected)
    {
        var workflow = CreateWorkflow("wf", "dom");

        CreateService().ClassifyKey(workflow, transitionKey).ShouldBe(expected);
    }

    [Fact]
    public void ClassifyKey_ConfiguredCustomCancelKey_ReturnsBypass()
    {
        var workflow = CreateWorkflow("wf", "dom");
        workflow.SetCancel(Transition.Create("iptal-et", null, "state1", TriggerType.Manual, "Patch"));

        CreateService().ClassifyKey(workflow, "iptal-et").ShouldBe(AdmissionKind.BypassBusyCheck);
    }

    #endregion

    #region IsSubflowForward / Busy + aktif SubFlow muafiyeti

    [Fact]
    public void IsSubflowForward_BusyWithActiveSubflow_ReturnsTrue()
    {
        var context = CreateContext();
        context.Instance.Busy();
        AddActiveSubflowCorrelation(context.Instance);

        CreateService().IsSubflowForward(context).ShouldBeTrue();
    }

    [Fact]
    public void IsSubflowForward_BusyWithoutSubflow_ReturnsFalse()
    {
        // Not: aktif SubFlow korelasyonu eklemek domain kuralı gereği parent'ı zaten Busy yapar
        // (Instance.AddCorrelation → Busy()), bu yüzden "Active + subflow" diye bir durum yok.
        var context = CreateContext();
        context.Instance.Busy();

        CreateService().IsSubflowForward(context).ShouldBeFalse();
    }

    [Fact]
    public void CheckAdmission_BusyWithActiveSubflow_Succeeds()
    {
        // Forward edilecek — 409 yok; ForwardToActiveSubflowStep isteği subflow'a iletir.
        var context = CreateContext();
        context.Instance.Busy();
        AddActiveSubflowCorrelation(context.Instance);

        CreateService().CheckAdmission(context).IsSuccess.ShouldBeTrue();
    }

    #endregion

    #region ReleaseReservationAsync

    [Fact]
    public async Task ReleaseReservation_ReleasesViaBusyManager()
    {
        var context = CreateContext();
        SetupAcquiredLock();
        _busyManager
            .TryReleaseAsync(context.InstanceId, Arg.Any<CancellationToken>())
            .Returns(true);

        await CreateService().ReleaseReservationAsync(context, CancellationToken.None);

        await _busyManager.Received(1)
            .TryReleaseAsync(context.InstanceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseReservation_WhenBusyManagerThrows_DoesNotPropagate()
    {
        var context = CreateContext();
        SetupAcquiredLock();
        _busyManager
            .TryReleaseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("boom"));

        // Compensation must never mask the original failure.
        await Should.NotThrowAsync(
            () => CreateService().ReleaseReservationAsync(context, CancellationToken.None));
    }

    #endregion

    #region Helpers

    private static void AddActiveSubflowCorrelation(Instance instance)
        => instance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), instance.Id, "child-flow", Guid.NewGuid(),
            SubFlowType.SubFlow.Code, "child-domain", "child-flow", "1.0.0"));

    #region Subflow chain reserve

    [Fact]
    public async Task ReserveSubflowChainAsync_ShouldMarkWithPropagation_NotTheTryVariant()
    {
        // The relay levels are already Busy for their subflow's lifetime, so the Try- variant
        // would short-circuit on AlreadyBusy and never reach the leaf — the one level a
        // long-polling client actually observes.
        SetupAcquiredLock();
        var context = CreateContext();

        var result = await CreateService().ReserveSubflowChainAsync(context);

        result.IsSuccess.ShouldBeTrue();
        await _busyManager.Received(1).MarkBusyWithPropagationAsync(context.InstanceId, Arg.Any<CancellationToken>());
        await _busyManager.DidNotReceive().TryMarkBusyWithPropagationAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReserveSubflowChainAsync_WhenLockNotAcquired_ShouldFailAndNotMark()
    {
        SetupFailedLock();
        var context = CreateContext();

        var result = await CreateService().ReserveSubflowChainAsync(context);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConflictWorkflow);
        await _busyManager.DidNotReceive().MarkBusyWithPropagationAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseSubflowChainAsync_ShouldReleaseWithPropagationUnderTheLock()
    {
        SetupAcquiredLock();
        var context = CreateContext();

        await CreateService().ReleaseSubflowChainAsync(context);

        await _busyManager.Received(1).ReleaseWithPropagationAsync(context.InstanceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseSubflowChainAsync_WhenLockNotAcquired_ShouldNoOpWithoutThrowing()
    {
        SetupFailedLock();
        var context = CreateContext();

        await CreateService().ReleaseSubflowChainAsync(context);

        await _busyManager.DidNotReceive().ReleaseWithPropagationAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseSubflowChainAsync_WhenReleaseThrows_ShouldSwallow()
    {
        // Compensation must never mask the original failure.
        SetupAcquiredLock();
        var context = CreateContext();
        _busyManager
            .ReleaseWithPropagationAsync(context.InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        await Should.NotThrowAsync(() => CreateService().ReleaseSubflowChainAsync(context));
    }

    #endregion

    #region AcceptAsync — the accept path's single lock

    [Fact]
    public async Task AcceptAsync_WhenLockNotAcquired_ShouldFailAndNotRunTheCallback()
    {
        SetupFailedLock();
        var context = CreateContext();
        var ran = false;

        var result = await CreateService().AcceptAsync(
            context, (_, _) => { ran = true; return Task.FromResult(Result.Ok()); });

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConflictWorkflow);
        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task AcceptAsync_NormalTransition_ShouldReserveAndReportReserved()
    {
        SetupAcquiredLock();
        var context = CreateContext();
        _busyManager
            .TryMarkBusyWithPropagationAsync(context.InstanceId, Arg.Any<CancellationToken>())
            .Returns(BusyMarkOutcome.Marked);

        var seen = AcceptFlip.None;
        var result = await CreateService().AcceptAsync(
            context, (flip, _) => { seen = flip; return Task.FromResult(Result.Ok()); });

        result.IsSuccess.ShouldBeTrue();
        seen.ShouldBe(AcceptFlip.Reserved);
    }

    [Fact]
    public async Task AcceptAsync_NormalTransitionOnBusyInstance_ShouldFailWithoutRunningTheCallback()
    {
        SetupAcquiredLock();
        var context = CreateContext();
        context.Instance.Busy();
        var ran = false;

        var result = await CreateService().AcceptAsync(
            context, (_, _) => { ran = true; return Task.FromResult(Result.Ok()); });

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
        ran.ShouldBeFalse();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("exit")]
    public async Task AcceptAsync_CancelAndExit_ShouldFlipBusyUnderTheSameLock(string transitionKey)
    {
        // Exempt from the Busy 409, but they DO set the status — so they take the same lock as
        // everything else and do their work under it, instead of flipping later in the pipeline.
        SetupAcquiredLock();
        var context = CreateContext(transitionKey);
        _busyManager.MarkBusyAsync(context.InstanceId, Arg.Any<CancellationToken>()).Returns(true);

        var seen = AcceptFlip.None;
        var result = await CreateService().AcceptAsync(
            context, (flip, _) => { seen = flip; return Task.FromResult(Result.Ok()); });

        result.IsSuccess.ShouldBeTrue();
        seen.ShouldBe(AcceptFlip.TakenOver);
        await _busyManager.Received(1).MarkBusyAsync(context.InstanceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_CancelOnAnInstanceSomeoneElseHoldsBusy_ShouldReportNoFlip()
    {
        // The flip was a no-op, so there is nothing this accept may release — compensating would
        // free another owner's instance.
        SetupAcquiredLock();
        var context = CreateContext("cancel");
        _busyManager.MarkBusyAsync(context.InstanceId, Arg.Any<CancellationToken>()).Returns(false);

        var seen = AcceptFlip.Reserved;
        await CreateService().AcceptAsync(
            context, (flip, _) => { seen = flip; return Task.FromResult(Result.Ok()); });

        seen.ShouldBe(AcceptFlip.None);
    }

    [Fact]
    public async Task AcceptAsync_UpdateData_ShouldNotTouchTheStatus()
    {
        SetupAcquiredLock();
        var context = CreateContext("update-parent-data");

        var seen = AcceptFlip.Reserved;
        var result = await CreateService().AcceptAsync(
            context, (flip, _) => { seen = flip; return Task.FromResult(Result.Ok()); });

        result.IsSuccess.ShouldBeTrue();
        seen.ShouldBe(AcceptFlip.None);
        await _busyManager.DidNotReceive().MarkBusyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _busyManager.DidNotReceive().TryMarkBusyWithPropagationAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_OwnerReentry_ShouldNotFlipAgain()
    {
        SetupAcquiredLock();
        var context = CreateContext();
        context.IsPreReserved = true;

        var seen = AcceptFlip.Reserved;
        await CreateService().AcceptAsync(
            context, (flip, _) => { seen = flip; return Task.FromResult(Result.Ok()); });

        seen.ShouldBe(AcceptFlip.None);
        await _busyManager.DidNotReceive().TryMarkBusyWithPropagationAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_WhenCallbackFails_ShouldCompensateTheReserve()
    {
        SetupAcquiredLock();
        var context = CreateContext();
        _busyManager
            .TryMarkBusyWithPropagationAsync(context.InstanceId, Arg.Any<CancellationToken>())
            .Returns(BusyMarkOutcome.Marked);

        var result = await CreateService().AcceptAsync(
            context, (_, _) => Task.FromResult(Result.Fail(Error.Validation("x", "boom"))));

        result.IsSuccess.ShouldBeFalse();
        await _busyManager.Received(1).TryReleaseAsync(context.InstanceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_WhenCallbackThrows_ShouldCompensateAndRethrow()
    {
        SetupAcquiredLock();
        var context = CreateContext();
        _busyManager
            .TryMarkBusyWithPropagationAsync(context.InstanceId, Arg.Any<CancellationToken>())
            .Returns(BusyMarkOutcome.Marked);

        await Should.ThrowAsync<InvalidOperationException>(() => CreateService().AcceptAsync(
            context, (_, _) => throw new InvalidOperationException("boom")));

        await _busyManager.Received(1).TryReleaseAsync(context.InstanceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_WhenCallbackFailsWithoutAFlip_ShouldNotRelease()
    {
        // updateData never flipped anything; releasing here would settle someone else's Busy.
        SetupAcquiredLock();
        var context = CreateContext("update-parent-data");

        await CreateService().AcceptAsync(
            context, (_, _) => Task.FromResult(Result.Fail(Error.Validation("x", "boom"))));

        await _busyManager.DidNotReceive().TryReleaseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _busyManager.DidNotReceive().ReleaseWithPropagationAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_UpdateData_ShouldRunLockFree()
    {
        // updateData is status-neutral and must accept parallel requests: there is no flip to
        // serialize and the duplicate-job guard does not apply, so the accept never touches the
        // status lock — mirroring the sync path, which never locked this kind either.
        var context = CreateContext("update-parent-data");

        var seen = AcceptFlip.Reserved;
        var result = await CreateService().AcceptAsync(
            context, (flip, _) => { seen = flip; return Task.FromResult(Result.Ok()); });

        result.IsSuccess.ShouldBeTrue();
        seen.ShouldBe(AcceptFlip.None);
        await _statusLock.DidNotReceive().AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_UpdateData_WhenAnotherAcceptHoldsTheLock_ShouldStillAccept()
    {
        // THE behavioral change: N subprocesses notifying the parent simultaneously used to make
        // the losers fail with Instance:100002 and burn their error-boundary retry backoff.
        // A held status lock is now irrelevant to updateData — it is not even consulted.
        SetupFailedLock();
        var context = CreateContext("update-parent-data");
        var ran = false;

        var result = await CreateService().AcceptAsync(
            context, (_, _) => { ran = true; return Task.FromResult(Result.Ok()); });

        result.IsSuccess.ShouldBeTrue();
        ran.ShouldBeTrue();
    }

    [Fact]
    public async Task AcceptAsync_UpdateData_OnBusyInstance_ShouldStillAccept()
    {
        var context = CreateContext("update-parent-data");
        context.Instance.Busy();
        var ran = false;

        var result = await CreateService().AcceptAsync(
            context, (_, _) => { ran = true; return Task.FromResult(Result.Ok()); });

        result.IsSuccess.ShouldBeTrue();
        ran.ShouldBeTrue();
        await _statusLock.DidNotReceive().AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion

    private static TransitionExecutionContext CreateContext(
        string transitionKey = "regular-transition",
        bool isErrorBoundaryTransition = false)
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var workflow = CreateWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
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
            Current = workflow.GetState("state1").Value!,
            Transition = transition,
            Instance = instance,
            IsErrorBoundaryTransition = isErrorBoundaryTransition,
            Data = null,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow(string key, string domain)
    {
        var json = """
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
