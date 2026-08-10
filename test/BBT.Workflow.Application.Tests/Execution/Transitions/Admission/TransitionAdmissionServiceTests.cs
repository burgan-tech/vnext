using System;
using System.Threading;
using System.Threading.Tasks;
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

    private static TransitionExecutionContext CreateContext(string transitionKey = "regular-transition")
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
