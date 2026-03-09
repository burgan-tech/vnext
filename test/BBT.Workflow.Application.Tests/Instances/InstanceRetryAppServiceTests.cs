using System;
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
