using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.PostCommit.Handlers;
using BBT.Workflow.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.PostCommit.Handlers;

/// <summary>
/// Unit tests for ForwardToSubflowJobHandler.
/// Tests Result pattern handling and ClientResponse.Error population.
/// </summary>
public class ForwardToSubflowJobHandlerTests
{
    private readonly ISubflowForwardingService _mockForwardingService;
    private readonly IInstanceRepository _mockInstanceRepository;
    private readonly ILogger<ForwardToSubflowJobHandler> _mockLogger;
    private readonly ForwardToSubflowJobHandler _handler;

    public ForwardToSubflowJobHandlerTests()
    {
        _mockForwardingService = Substitute.For<ISubflowForwardingService>();
        _mockInstanceRepository = Substitute.For<IInstanceRepository>();
        _mockLogger = Substitute.For<ILogger<ForwardToSubflowJobHandler>>();
        _handler = new ForwardToSubflowJobHandler(_mockForwardingService, _mockInstanceRepository, _mockLogger);
    }

    [Fact]
    public async Task HandleAsync_WhenForwardSucceeds_ShouldSetClientResponseAndReturnSuccess()
    {
        // Arrange
        var job = CreateForwardToSubflowJob();
        var context = CreateContext();
        var forwardResult = Result<TransitionOutput>.Ok(new TransitionOutput
        {
            Id = job.SubflowInstanceId,
            Status = InstanceStatus.Active
        });

        _mockForwardingService
            .ForwardTransitionAsync(job.SubflowInstanceId, job.TransitionKey, Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(forwardResult);

        // Act
        var result = await _handler.HandleAsync(job, context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.ClientResponse.ShouldNotBeNull();
        context.ClientResponse!.Id.ShouldBe(context.InstanceId);
        context.ClientResponse.Status.ShouldBe(InstanceStatus.Active);
        context.ClientResponse.Error.ShouldBeNull();
    }

    [Fact]
    public async Task HandleAsync_WhenSubflowCompleted_ShouldUseFreshParentStatus()
    {
        // Arrange — the tracked context.Instance is stale (Busy); the subflow's completion
        // resumed and finalized the parent in another scope, so the fresh read returns Completed.
        var job = CreateForwardToSubflowJob();
        var context = CreateContext();
        context.Instance.Busy();

        var freshParent = Instance.Create(context.InstanceId, "sys_flows", "1.0.0", "test-key");
        freshParent.Complete("test-domain");
        _mockInstanceRepository
            .FindByIdentifierSlimAsync(context.InstanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(freshParent);

        var forwardResult = Result<TransitionOutput>.Ok(new TransitionOutput
        {
            Id = job.SubflowInstanceId,
            Status = InstanceStatus.Completed // Subflow completed
        });

        _mockForwardingService
            .ForwardTransitionAsync(job.SubflowInstanceId, job.TransitionKey, Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(forwardResult);

        // Act
        var result = await _handler.HandleAsync(job, context, CancellationToken.None);

        // Assert — settled parent status from the fresh read, not the stale Busy snapshot
        result.IsSuccess.ShouldBeTrue();
        context.ClientResponse.ShouldNotBeNull();
        context.ClientResponse!.Status.ShouldBe(InstanceStatus.Completed);
    }

    [Fact]
    public async Task HandleAsync_WhenSubflowCompletedAndFreshReadReturnsNothing_ShouldFallBackToSnapshotStatus()
    {
        // Arrange
        var job = CreateForwardToSubflowJob();
        var context = CreateContext();
        // Note: Instance.Status defaults to Active after creation; repository substitute returns null

        var forwardResult = Result<TransitionOutput>.Ok(new TransitionOutput
        {
            Id = job.SubflowInstanceId,
            Status = InstanceStatus.Completed // Subflow completed
        });

        _mockForwardingService
            .ForwardTransitionAsync(job.SubflowInstanceId, job.TransitionKey, Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(forwardResult);

        // Act
        var result = await _handler.HandleAsync(job, context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.ClientResponse.ShouldNotBeNull();
        // Should use parent instance status, not subflow's Completed status
        context.ClientResponse!.Status.ShouldBe(InstanceStatus.Active);
    }

    [Fact]
    public async Task HandleAsync_WhenForwardFailsWithValidationError_ShouldSetClientResponseErrorAndReturnFailure()
    {
        // Arrange
        var job = CreateForwardToSubflowJob();
        var context = CreateContext();
        var validationError = Error.Validation("Transition:100020", "Transition not available in current state");
        
        _mockForwardingService
            .ForwardTransitionAsync(job.SubflowInstanceId, job.TransitionKey, Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(Result<TransitionOutput>.Fail(validationError));

        // Act
        var result = await _handler.HandleAsync(job, context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(validationError);
        
        context.ClientResponse.ShouldNotBeNull();
        context.ClientResponse!.Error.ShouldNotBeNull();
        context.ClientResponse.Error!.Value.Code.ShouldBe("Transition:100020");
        context.ClientResponse.Error.Value.Prefix.ShouldBe(ErrorCodes.Prefixes.Validation);
        context.ClientResponse.Status.ShouldBe(context.Instance.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenForwardFailsWithSystemError_ShouldSetClientResponseErrorAndReturnFailure()
    {
        // Arrange
        var job = CreateForwardToSubflowJob();
        var context = CreateContext();
        var systemError = Error.Dependency("remote_service_error", "Remote API service error");
        
        _mockForwardingService
            .ForwardTransitionAsync(job.SubflowInstanceId, job.TransitionKey, Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(Result<TransitionOutput>.Fail(systemError));

        // Act
        var result = await _handler.HandleAsync(job, context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(systemError);
        
        context.ClientResponse.ShouldNotBeNull();
        context.ClientResponse!.Error.ShouldNotBeNull();
        context.ClientResponse.Error!.Value.Prefix.ShouldBe(ErrorCodes.Prefixes.Dependency);
    }

    [Theory]
    [InlineData(ExecMode.Sync, true)]
    [InlineData(ExecMode.Async, false)]
    public async Task HandleAsync_ShouldPropagateSyncFromCallerMode(ExecMode callerMode, bool expectedSync)
    {
        // Arrange
        var job = CreateForwardToSubflowJob();
        var context = CreateContext(callerMode: callerMode);

        TransitionInput? capturedInput = null;
        _mockForwardingService
            .ForwardTransitionAsync(
                job.SubflowInstanceId,
                job.TransitionKey,
                Arg.Do<TransitionInput>(input => capturedInput = input),
                Arg.Any<CancellationToken>(),
                Arg.Any<Guid?>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = job.SubflowInstanceId,
                Status = InstanceStatus.Active
            }));

        // Act
        await _handler.HandleAsync(job, context, CancellationToken.None);

        // Assert
        capturedInput.ShouldNotBeNull();
        capturedInput!.Sync.ShouldBe(expectedSync);
    }

    [Fact]
    public async Task HandleAsync_WhenModeIsSyncButCallerModeIsAsync_ShouldUseCallerModeForSubflow()
    {
        // Arrange — simulates a background job handler scenario where Mode=Sync (loop prevention)
        // but CallerMode=Async (original caller wanted async)
        var job = CreateForwardToSubflowJob();
        var context = CreateContext(mode: ExecMode.Sync, callerMode: ExecMode.Async);

        TransitionInput? capturedInput = null;
        _mockForwardingService
            .ForwardTransitionAsync(
                job.SubflowInstanceId,
                job.TransitionKey,
                Arg.Do<TransitionInput>(input => capturedInput = input),
                Arg.Any<CancellationToken>(),
                Arg.Any<Guid?>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = job.SubflowInstanceId,
                Status = InstanceStatus.Active
            }));

        // Act
        await _handler.HandleAsync(job, context, CancellationToken.None);

        // Assert — subflow should receive sync=false (from CallerMode), not sync=true (from Mode)
        capturedInput.ShouldNotBeNull();
        capturedInput!.Sync.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_ShouldCarryChainReservedClaimOntoTheForwardedInput(bool chainReserved)
    {
        // The claim is what stops the leaf — which the accept already flipped Busy — from
        // rejecting this relay with a 409.
        var job = CreateForwardToSubflowJob(chainReserved);
        var context = CreateContext();

        TransitionInput? capturedInput = null;
        _mockForwardingService
            .ForwardTransitionAsync(
                job.SubflowInstanceId,
                job.TransitionKey,
                Arg.Do<TransitionInput>(input => capturedInput = input),
                Arg.Any<CancellationToken>(),
                Arg.Any<Guid?>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = job.SubflowInstanceId,
                Status = InstanceStatus.Active
            }));

        await _handler.HandleAsync(job, context, CancellationToken.None);

        capturedInput.ShouldNotBeNull();
        capturedInput!.ChainReserved.ShouldBe(chainReserved);
    }

    private static ForwardToSubflowJob CreateForwardToSubflowJob(bool chainReserved = false)
    {
        return new ForwardToSubflowJob(
            SubflowInstanceId: Guid.NewGuid(),
            ParentInstanceId: Guid.NewGuid(),
            TransitionKey: "test-transition",
            SubflowDomain: "test-domain",
            SubflowName: "test-subflow",
            SubflowVersion: "1.0.0",
            InstanceKey: "test-key",
            Stage: "initial",
            Tags: null,
            DataElement: JsonDocument.Parse("{}").RootElement,
            Headers: new Dictionary<string, string?>(),
            RouteValues: new Dictionary<string, string?>(),
            ChainReserved: chainReserved
        );
    }

    private static TransitionExecutionContext CreateContext(ExecMode mode = ExecMode.Sync, ExecMode? callerMode = null)
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "sys_flows", "1.0.0","test-key");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Instance = instance,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            Mode = mode,
            CallerMode = callerMode ?? mode
        };
    }
}
