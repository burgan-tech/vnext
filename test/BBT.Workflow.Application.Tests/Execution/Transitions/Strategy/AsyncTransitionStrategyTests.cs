using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Strategies;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Strategy;

/// <summary>
/// Unit tests for AsyncTransitionStrategy
/// Tests asynchronous transition execution strategy via background jobs
/// </summary>
public class AsyncTransitionStrategyTests
{
    private readonly Mock<IBackgroundJobService> _mockBackgroundJobService;
    private readonly Mock<ITransitionContextFactory> _mockContextFactory;
    private readonly Mock<IInstanceJobRepository> _mockJobRepository;
    private readonly Mock<ILogger<AsyncTransitionStrategy>> _mockLogger;
    private readonly AsyncTransitionStrategy _strategy;

    public AsyncTransitionStrategyTests()
    {
        _mockBackgroundJobService = new Mock<IBackgroundJobService>();
        _mockContextFactory = new Mock<ITransitionContextFactory>();
        _mockJobRepository = new Mock<IInstanceJobRepository>();
        _mockLogger = new Mock<ILogger<AsyncTransitionStrategy>>();

        _strategy = new AsyncTransitionStrategy(
            _mockBackgroundJobService.Object,
            _mockContextFactory.Object,
            _mockJobRepository.Object,
            _mockLogger.Object);
    }

    #region ExecuteAsync Tests

    [Fact]
    public async Task ExecuteAsync_WithValidContext_ShouldEnqueueJobSuccessfully()
    {
        // Arrange
        var workflowContext = CreateWorkflowExecutionContext();
        var transitionContext = CreateTransitionExecutionContext();

        SetupSuccessfulExecution(workflowContext, transitionContext);

        // Act
        var result = await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();

        _mockBackgroundJobService.Verify(
            x => x.EnqueueAsync(
                TransitionJobHandler.HandlerName,
                It.Is<string>(id => id.StartsWith($"trans-{workflowContext.InstanceId}")),
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCreateCorrectJobPayload()
    {
        // Arrange
        var workflowContext = CreateWorkflowExecutionContext();
        var transitionContext = CreateTransitionExecutionContext();
        TransitionJobPayload? capturedPayload = null;

        SetupSuccessfulExecution(workflowContext, transitionContext);

        _mockBackgroundJobService
            .Setup(x => x.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, TransitionJobPayload, string, Dictionary<string, object>, CancellationToken>(
                (_, _, payload, _, _, _) => capturedPayload = payload)
            .ReturnsAsync(It.IsAny<Guid>());

        // Act
        await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);

        // Assert
        capturedPayload!.ShouldNotBeNull();
        capturedPayload.InstanceId.ToString().ShouldBe(workflowContext.InstanceId);
        capturedPayload.TransitionKey.ShouldBe(workflowContext.TransitionKey);
        capturedPayload.Domain.ShouldBe(workflowContext.Domain);
        capturedPayload.Workflow.ShouldBe(workflowContext.WorkflowKey);
        capturedPayload.ExecutionActor.ShouldBe(workflowContext.Actor);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCreateCorrectMetadata()
    {
        // Arrange
        var workflowContext = CreateWorkflowExecutionContext();
        var transitionContext = CreateTransitionExecutionContext();
        Dictionary<string, object>? capturedMetadata = null;

        SetupSuccessfulExecution(workflowContext, transitionContext);

        _mockBackgroundJobService
            .Setup(x => x.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, TransitionJobPayload, string, Dictionary<string, object>, CancellationToken>(
                (_, _, _, _, metadata, _) => capturedMetadata = metadata)
            .ReturnsAsync(It.IsAny<Guid>());

        // Act
        await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);

        // Assert
        capturedMetadata.ShouldNotBeNull();
        capturedMetadata["domain"].ShouldBe((object)workflowContext.Domain);
        capturedMetadata["flowName"].ShouldBe((object)workflowContext.WorkflowKey);
        capturedMetadata["instanceId"].ShouldBe((object)workflowContext.InstanceId.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldGenerateUniqueJobId()
    {
        // Arrange
        var workflowContext = CreateWorkflowExecutionContext();
        var transitionContext = CreateTransitionExecutionContext();
        var jobIds = new List<string>();

        SetupSuccessfulExecution(workflowContext, transitionContext);

        _mockBackgroundJobService
            .Setup(x => x.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, TransitionJobPayload, string, Dictionary<string, object>, CancellationToken>(
                (_, jobId, _, _, _, _) => jobIds.Add(jobId))
            .ReturnsAsync(It.IsAny<Guid>());

        // Act
        await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);
        await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);

        // Assert
        jobIds.Count.ShouldBe(2);
        jobIds[0].ShouldNotBe(jobIds[1]);
    }

    [Fact]
    public async Task ExecuteAsync_WhenContextCreationFails_ShouldReturnFailure()
    {
        // Arrange
        var workflowContext = CreateWorkflowExecutionContext();
        var error = Error.NotFound("instance.notfound", "Instance not found");

        _mockContextFactory
            .Setup(x => x.CreateAsync(workflowContext, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionExecutionContext>.Fail(error));

        // Act
        var result = await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(error);

        _mockBackgroundJobService.Verify(
            x => x.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnqueueFails_ShouldReturnFailure()
    {
        // Arrange
        var workflowContext = CreateWorkflowExecutionContext();
        var transitionContext = CreateTransitionExecutionContext();

        _mockContextFactory
            .Setup(x => x.CreateAsync(workflowContext, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionExecutionContext>.Ok(transitionContext));

        _mockBackgroundJobService
            .Setup(x => x.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Job service unavailable"));

        // Act
        var result = await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldScheduleJobForImmediateExecution()
    {
        // Arrange
        var workflowContext = CreateWorkflowExecutionContext();
        var transitionContext = CreateTransitionExecutionContext();
        string? capturedSchedule = null;

        SetupSuccessfulExecution(workflowContext, transitionContext);

        _mockBackgroundJobService
            .Setup(x => x.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, TransitionJobPayload, string, Dictionary<string, object>, CancellationToken>(
                (_, _, _, schedule, _, _) => capturedSchedule = schedule)
            .ReturnsAsync(It.IsAny<Guid>());

        // Act
        await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);

        // Assert
        capturedSchedule.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldLogExecutionStartAndComplete()
    {
        // Arrange
        var workflowContext = CreateWorkflowExecutionContext();
        var transitionContext = CreateTransitionExecutionContext();

        SetupSuccessfulExecution(workflowContext, transitionContext);

        // Act
        await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("enqueued") || v.ToString()!.Contains("Successfully")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeast(1));
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellation_ShouldPropagateCancellation()
    {
        // Arrange
        var workflowContext = CreateWorkflowExecutionContext();
        CreateTransitionExecutionContext();
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _mockContextFactory
            .Setup(x => x.CreateAsync(workflowContext, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _strategy.ExecuteAsync(workflowContext, cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_WithHeadersAndRouteValues_ShouldIncludeInPayload()
    {
        // Arrange
        var headers = new Dictionary<string, string?> { ["X-Custom-Header"] = "value" };
        var routeValues = new Dictionary<string, string?> { ["id"] = "123" };
        
        var workflowContext = CreateWorkflowExecutionContext();
        workflowContext.Headers = headers;
        workflowContext.RouteValues = routeValues;

        var transitionContext = CreateTransitionExecutionContext();
        TransitionJobPayload? capturedPayload = null;

        SetupSuccessfulExecution(workflowContext, transitionContext);

        _mockBackgroundJobService
            .Setup(x => x.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, TransitionJobPayload, string, Dictionary<string, object>, CancellationToken>(
                (_, _, payload, _, _, _) => capturedPayload = payload)
            .ReturnsAsync(It.IsAny<Guid>());

        // Act
        await _strategy.ExecuteAsync(workflowContext, CancellationToken.None);

        // Assert
        capturedPayload.ShouldNotBeNull();
        capturedPayload.Headers.ShouldBe(headers);
        capturedPayload.RouteValues.ShouldBe(routeValues);
    }

    #endregion

    #region Helper Methods

    private void SetupSuccessfulExecution(WorkflowExecutionContext workflowContext, TransitionExecutionContext transitionContext)
    {
        _mockContextFactory
            .Setup(x => x.CreateAsync(workflowContext, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionExecutionContext>.Ok(transitionContext));

        _mockBackgroundJobService
            .Setup(x => x.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TransitionJobPayload>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(It.IsAny<Guid>());

        _mockJobRepository
            .Setup(x => x.InsertAsync(
                It.IsAny<InstanceJob>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(It.IsAny<InstanceJob>());
    }

    private WorkflowExecutionContext CreateWorkflowExecutionContext()
    {
        return new WorkflowExecutionContext
        {
            InstanceId = Guid.NewGuid().ToString(),
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            TriggerType = TriggerType.Manual,
            Actor = ExecutionActor.User,
            Data = null,
            Headers = new Dictionary<string, string?>(),
            RouteValues = new Dictionary<string, string?>()
        };
    }

    private TransitionExecutionContext CreateTransitionExecutionContext()
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create("test-transition", null, "state1", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "test-transition",
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

    private Definitions.Workflow CreateMockWorkflow(string key, string domain)
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

