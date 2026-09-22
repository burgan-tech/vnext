using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for IInstanceRetryGateway interface behavior.
/// Tests verify correct gateway routing and result handling.
/// Note: Full integration tests for InstanceRetryAppService should use ApplicationTestBase infrastructure.
/// </summary>
public class InstanceRetryGatewayTests
{
    private readonly IInstanceRetryGateway _retryGateway;
    private readonly IInstanceQueryGateway _queryGateway;

    public InstanceRetryGatewayTests()
    {
        _retryGateway = Substitute.For<IInstanceRetryGateway>();
        _queryGateway = Substitute.For<IInstanceQueryGateway>();
    }

    [Fact]
    public async Task RetryGateway_WhenCalled_ShouldReturnSuccessfulResult()
    {
        // Arrange
        var input = CreateRetryInput("test-instance");
        var expectedOutput = new RetryInstanceOutput
        {
            Id = Guid.NewGuid(),
            Status = InstanceStatus.Active,
            RetriedTransitionId = Guid.NewGuid()
        };

        _retryGateway.RetryAsync(input, Arg.Any<CancellationToken>())
            .Returns(Result<RetryInstanceOutput>.Ok(expectedOutput));

        // Act
        var result = await _retryGateway.RetryAsync(input);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Id.ShouldBe(expectedOutput.Id);
        result.Value.Status.ShouldBe(InstanceStatus.Active);
    }

    [Fact]
    public async Task RetryGateway_WhenInstanceNotFound_ShouldReturnNotFoundError()
    {
        // Arrange
        var input = CreateRetryInput("non-existent");
        _retryGateway.RetryAsync(input, Arg.Any<CancellationToken>())
            .Returns(Result<RetryInstanceOutput>.Fail(Error.NotFound(
                WorkflowErrorCodes.InstanceNotFound,
                "Instance not found")));

        // Act
        var result = await _retryGateway.RetryAsync(input);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceNotFound);
    }

    [Fact]
    public async Task RetryGateway_WhenInstanceNotFaulted_ShouldReturnValidationError()
    {
        // Arrange
        var input = CreateRetryInput("active-instance");
        _retryGateway.RetryAsync(input, Arg.Any<CancellationToken>())
            .Returns(Result<RetryInstanceOutput>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceNotFaulted,
                "Instance is not in faulted state")));

        // Act
        var result = await _retryGateway.RetryAsync(input);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceNotFaulted);
    }

    [Fact]
    public async Task RetryGateway_WhenRetryFaultsAgain_ShouldReturnFaultedStatus()
    {
        // Arrange
        var input = CreateRetryInput("failing-instance");
        var expectedOutput = new RetryInstanceOutput
        {
            Id = Guid.NewGuid(),
            Status = InstanceStatus.Faulted,
            RetriedTransitionId = Guid.NewGuid()
        };

        _retryGateway.RetryAsync(input, Arg.Any<CancellationToken>())
            .Returns(Result<RetryInstanceOutput>.Ok(expectedOutput));

        // Act
        var result = await _retryGateway.RetryAsync(input);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(InstanceStatus.Faulted);
    }

    [Fact]
    public async Task RetryGateway_WithCrossDomainSubflow_ShouldRouteToRemote()
    {
        // Arrange
        var input = new RetryInstanceInput
        {
            Domain = "remote-domain",
            Workflow = "subflow-workflow",
            Instance = Guid.NewGuid().ToString(),
            Sync = false
        };
        var expectedOutput = new RetryInstanceOutput
        {
            Id = Guid.Parse(input.Instance),
            Status = InstanceStatus.Active,
            RetriedTransitionId = Guid.NewGuid()
        };

        _retryGateway.RetryAsync(
            Arg.Is<RetryInstanceInput>(r => r.Domain == "remote-domain"),
            Arg.Any<CancellationToken>())
            .Returns(Result<RetryInstanceOutput>.Ok(expectedOutput));

        // Act
        var result = await _retryGateway.RetryAsync(input);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Id.ShouldBe(expectedOutput.Id);

        await _retryGateway.Received(1).RetryAsync(
            Arg.Is<RetryInstanceInput>(r => r.Domain == "remote-domain"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryGateway_WhenQueryingSubflowState_ShouldReturnFaultedStatus()
    {
        // Arrange
        var input = new GetFunctionWithInstanceInput
        {
            Domain = "subflow-domain",
            Workflow = "subflow-workflow",
            Instance = Guid.NewGuid().ToString()
        };
        var expectedOutput = new GetInstanceStateOutput
        {
            Status = InstanceStatus.Faulted,
            State = "faulted-state"
        };

        _queryGateway.GetFunctionWithStateAsync(input, Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceStateOutput>.Success(expectedOutput));

        // Act
        var result = await _queryGateway.GetFunctionWithStateAsync(input);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Status.ShouldBe(InstanceStatus.Faulted);
    }

    [Fact]
    public async Task RetryGateway_WithTransitionData_ShouldPassDataToRetry()
    {
        // Arrange
        var input = new RetryInstanceInput
        {
            Domain = "test-domain",
            Workflow = "test-workflow",
            Instance = Guid.NewGuid().ToString(),
            Sync = true,
            Data = new TransitionDataInput
            {
                Key = "retry-key"
            }
        };
        var expectedOutput = new RetryInstanceOutput
        {
            Id = Guid.Parse(input.Instance),
            Status = InstanceStatus.Active,
            RetriedTransitionId = Guid.NewGuid()
        };

        _retryGateway.RetryAsync(
            Arg.Is<RetryInstanceInput>(r => r.Data != null && r.Data.Key == "retry-key"),
            Arg.Any<CancellationToken>())
            .Returns(Result<RetryInstanceOutput>.Ok(expectedOutput));

        // Act
        var result = await _retryGateway.RetryAsync(input);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _retryGateway.Received(1).RetryAsync(
            Arg.Is<RetryInstanceInput>(r => r.Data != null && r.Data.Key == "retry-key"),
            Arg.Any<CancellationToken>());
    }

    private static RetryInstanceInput CreateRetryInput(string instanceId, string? domain = null) => new()
    {
        Domain = domain ?? "test-domain",
        Workflow = "test-workflow",
        Instance = instanceId,
        Sync = false
    };
}

/// <summary>
/// Unit tests for the classification/lookup logic <c>InstanceRetryAppService</c> uses to detect a
/// SubFlow correlation whose child was never created (a failed post-commit <c>StartSubflowJob</c>)
/// and recover it, instead of unconditionally delegating to a child that does not exist.
/// <para>
/// These two static helpers are the only pieces of the new retry-restart branch that can be pinned
/// without driving the whole <c>InstanceRetryAppService</c> through its <c>ApplicationService</c> /
/// <c>IUnitOfWorkManager</c> / distributed-lock infrastructure (the service has no test harness of
/// its own today — see the class remarks above). The end-to-end behaviour — probe-before-unfault,
/// the Busy re-arm, the actual subflow restart, and re-faulting the parent when the restart itself
/// fails — is proven against a running runtime by
/// <c>SubflowStartFailureLabTests</c> in the vnext-example integration suite, per this task's own
/// "unit tests are NOT sufficient here" directive.
/// </para>
/// </summary>
public class InstanceRetryAppServiceRestartLogicTests
{
    [Fact]
    public void IsChildInstanceMissing_ForTheNotFoundInstanceDataError_ReturnsTrue()
    {
        // This is exactly what GetFunctionWithStateAsync returns (WorkflowErrors.InstanceNotFound)
        // when the SubFlow correlation's child instance id does not exist — measured on the running
        // runtime as HTTP 404 "notfound.Instance:100013".
        var error = Error.NotFound(WorkflowErrorCodes.NotFoundInstanceData, "Instance \"x\" not found", "x");

        InstanceRetryAppService.IsChildInstanceMissing(error).ShouldBeTrue();
    }

    [Fact]
    public void IsChildInstanceMissing_ForADifferentNotFoundCode_ReturnsFalse()
    {
        // Same Prefix (notfound) but a different code — must not be misread as "child missing".
        var error = Error.NotFound(WorkflowErrorCodes.ActiveIncidentNotFound, "no active incident", "x");

        InstanceRetryAppService.IsChildInstanceMissing(error).ShouldBeFalse();
    }

    [Fact]
    public void IsChildInstanceMissing_ForANonNotFoundError_ReturnsFalse()
    {
        // Same code text is not enough without the NotFound prefix/category — a probe failure from
        // a transient/validation/dependency error must never be treated as "restart the child".
        var error = Error.Validation(WorkflowErrorCodes.NotFoundInstanceData, "coincidentally same code");

        InstanceRetryAppService.IsChildInstanceMissing(error).ShouldBeFalse();
    }

    [Fact]
    public void FindOriginatingTransition_WithNoMatch_ReturnsNull()
    {
        var transitions = new List<InstanceTransitionSlim>
        {
            MakeTransition("start", "parent-initial", DateTime.UtcNow.AddMinutes(-2))
        };

        InstanceRetryAppService.FindOriginatingTransition(transitions, "parent-subflow-state").ShouldBeNull();
    }

    [Fact]
    public void FindOriginatingTransition_WithOneMatch_ReturnsIt()
    {
        var target = MakeTransition("auto-parent-to-subflow", "parent-subflow-state", DateTime.UtcNow.AddMinutes(-1));
        var transitions = new List<InstanceTransitionSlim>
        {
            MakeTransition("start", "parent-initial", DateTime.UtcNow.AddMinutes(-2)),
            target
        };

        InstanceRetryAppService.FindOriginatingTransition(transitions, "parent-subflow-state")
            .ShouldBe(target);
    }

    [Fact]
    public void FindOriginatingTransition_WithTheStateReEntered_ReturnsTheLatestOne()
    {
        var earlier = MakeTransition("auto-parent-to-subflow", "parent-subflow-state", DateTime.UtcNow.AddMinutes(-5));
        var later = MakeTransition("retry-loop-back", "parent-subflow-state", DateTime.UtcNow.AddMinutes(-1));
        var transitions = new List<InstanceTransitionSlim> { earlier, later };

        InstanceRetryAppService.FindOriginatingTransition(transitions, "parent-subflow-state")
            .ShouldBe(later);
    }

    [Fact]
    public void FindOriginatingTransition_IgnoresIncompleteTransitions()
    {
        // ToState is null for a failed/incomplete transition (InstanceTransition.ToState doc
        // comment) — an in-flight/faulted hop must never be mistaken for the one that actually
        // moved the instance into the SubFlow state.
        var incomplete = MakeTransition("some-other-hop", toState: null, DateTime.UtcNow);
        var transitions = new List<InstanceTransitionSlim> { incomplete };

        InstanceRetryAppService.FindOriginatingTransition(transitions, "parent-subflow-state").ShouldBeNull();
    }

    private static InstanceTransitionSlim MakeTransition(string transitionId, string? toState, DateTime startedAt) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            transitionId,
            FromState: "irrelevant",
            ToState: toState,
            StartedAt: startedAt,
            FinishedAt: toState != null ? startedAt.AddSeconds(1) : null,
            Duration: null,
            TriggerType: TriggerType.Automatic,
            CreatedAt: startedAt,
            CreatedBy: null,
            CreatedByBehalfOf: null);
}
