using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// Unit tests for SubflowForwardingService.
/// Tests Result pattern propagation and error handling.
/// </summary>
public class SubflowForwardingServiceTests
{
    private readonly IInstanceCommandGateway _mockGateway;
    private readonly SubflowForwardingService _service;
    private readonly ILogger<SubflowForwardingService> _mockLogger;

    public SubflowForwardingServiceTests()
    {
        _mockGateway = Substitute.For<IInstanceCommandGateway>();
        _mockLogger = Substitute.For<ILogger<SubflowForwardingService>>();
        _service = new SubflowForwardingService(_mockGateway, _mockLogger);
    }

    [Fact]
    public async Task ForwardTransitionAsync_WhenGatewaySucceeds_ShouldReturnSuccessResult()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var transitionKey = "test-transition";
        var input = CreateTransitionInput();
        var expectedOutput = new TransitionOutput
        {
            Id = instanceId,
            Status = InstanceStatus.Active
        };

        _mockGateway
            .TransitionAsync(instanceId, transitionKey, input, Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(expectedOutput));

        // Act
        var result = await _service.ForwardTransitionAsync(instanceId, transitionKey, input, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Id.ShouldBe(instanceId);
        result.Value.Status.ShouldBe(InstanceStatus.Active);
    }

    [Fact]
    public async Task ForwardTransitionAsync_WhenGatewayFailsWithValidationError_ShouldReturnFailureResult()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var transitionKey = "invalid-transition";
        var input = CreateTransitionInput();
        var validationError = Error.Validation("Transition:100020", "Transition not available in current state");

        _mockGateway
            .TransitionAsync(instanceId, transitionKey, input, Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Fail(validationError));

        // Act
        var result = await _service.ForwardTransitionAsync(instanceId, transitionKey, input, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Transition:100020");
        result.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.Validation);
    }

    [Fact]
    public async Task ForwardTransitionAsync_WhenGatewayFailsWithSystemError_ShouldReturnFailureResult()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var transitionKey = "test-transition";
        var input = CreateTransitionInput();
        var systemError = Error.Dependency("remote_service_error", "Remote API service error");

        _mockGateway
            .TransitionAsync(instanceId, transitionKey, input, Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Fail(systemError));

        // Act
        var result = await _service.ForwardTransitionAsync(instanceId, transitionKey, input, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.Dependency);
    }

    [Fact]
    public async Task ForwardTransitionAsync_WhenSubflowCompleted_ShouldReturnCompletedStatus()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var transitionKey = "complete-transition";
        var input = CreateTransitionInput();
        var expectedOutput = new TransitionOutput
        {
            Id = instanceId,
            Status = InstanceStatus.Completed
        };

        _mockGateway
            .TransitionAsync(instanceId, transitionKey, input, Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(expectedOutput));

        // Act
        var result = await _service.ForwardTransitionAsync(instanceId, transitionKey, input, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(InstanceStatus.Completed);
    }

    private static TransitionInput CreateTransitionInput()
    {
        return new TransitionInput(
            "test-domain",
            "test-workflow",
            "1.0.0",
            new TransitionDataInput(null),
            true
        );
    }
}
