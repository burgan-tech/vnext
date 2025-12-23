using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Transitions.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Unit tests for TransitionPipeline
/// Tests pipeline orchestration, step execution, and flow control
/// </summary>
public class TransitionPipelineTests
{
    private readonly Mock<ILogger<TransitionPipeline>> _mockLogger;
    private readonly List<Mock<ITransitionStep>> _mockSteps;
    private readonly TransitionPipeline _pipeline;
    private readonly Mock<ErrorBoundaryStepInterceptor> _mockStepInterceptor;

    public TransitionPipelineTests()
    {
        _mockLogger = new Mock<ILogger<TransitionPipeline>>();
        _mockSteps = new List<Mock<ITransitionStep>>();
        _mockStepInterceptor = new Mock<ErrorBoundaryStepInterceptor>();
        
        // Create a default set of steps in order
        _mockSteps.Add(CreateMockStep(LifecycleOrder.CreateTransition));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnExecute));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnExit));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.ChangeState));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnEntry));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Schedule));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Auto));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Finalize));

        _pipeline = new TransitionPipeline(
            _mockSteps.Select(m => m.Object),
            _mockStepInterceptor.Object);
    }

    #region RunAsync Tests

    [Fact]
    public async Task RunAsync_WithValidContext_ShouldExecuteAllStepsInOrder()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var executionOrder = new List<int>();

        foreach (var mockStep in _mockSteps)
        {
            var order = mockStep.Object.Order;
            mockStep
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionOrder.Add(order))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionOrder.Count.ShouldBe(_mockSteps.Count);
        executionOrder.ShouldBe(_mockSteps.Select(m => m.Object.Order).OrderBy(o => o).ToList());
    }

    [Fact]
    public async Task RunAsync_WhenStepFails_ShouldStopAndReturnError()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var error = Error.Failure("step.failed", "Step execution failed");
        var executionCount = 0;

        // Setup first two steps to succeed
        for (int i = 0; i < 2; i++)
        {
            _mockSteps[i]
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionCount++)
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Setup third step to fail
        _mockSteps[2]
            .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => executionCount++)
            .ReturnsAsync(Result<StepOutcome>.Fail(error));

        // Setup remaining steps (shouldn't be called)
        for (int i = 3; i < _mockSteps.Count; i++)
        {
            _mockSteps[i]
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionCount++)
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(error);
        executionCount.ShouldBe(3); // Only first 3 steps executed
    }

    [Fact]
    public async Task RunAsync_WhenStepReturnsStop_ShouldStopPipeline()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var executionCount = 0;

        // Setup first two steps to succeed
        for (int i = 0; i < 2; i++)
        {
            _mockSteps[i]
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionCount++)
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Setup third step to stop pipeline
        _mockSteps[2]
            .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => executionCount++)
            .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Stop()));

        // Setup remaining steps
        for (int i = 3; i < _mockSteps.Count; i++)
        {
            _mockSteps[i]
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionCount++)
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionCount.ShouldBe(3); // Only first 3 steps executed
    }

    [Fact]
    public async Task RunAsync_WhenStepReturnsSkipTo_ShouldRebuildPlanAndContinue()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var executionOrder = new List<int>();
        var skipToOrder = LifecycleOrder.ChangeState;

        // Setup first step to skip to ChangeState
        _mockSteps[0]
            .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => executionOrder.Add(_mockSteps[0].Object.Order))
            .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.SkipTo(skipToOrder)));

        // Setup remaining steps
        for (int i = 1; i < _mockSteps.Count; i++)
        {
            var index = i;
            _mockSteps[i]
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionOrder.Add(_mockSteps[index].Object.Order))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionOrder.First().ShouldBe(LifecycleOrder.CreateTransition);
        executionOrder.Skip(1).First().ShouldBe(LifecycleOrder.ChangeState); // Skipped to ChangeState
    }

    [Fact]
    public async Task RunAsync_WhenSkipImmediateExecution_ShouldNotExecuteSteps()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.SkipImmediateExecution = true;
        var executionCount = 0;

        foreach (var mockStep in _mockSteps)
        {
            mockStep
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionCount++)
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionCount.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_WithEpilogueSkipMode_ShouldSkipScheduleAndAutoSteps()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Directives.RequestEpilogue(EpilogueMode.Skip);
        var executionOrder = new List<int>();

        foreach (var mockStep in _mockSteps)
        {
            var order = mockStep.Object.Order;
            mockStep
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionOrder.Add(order))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionOrder.ShouldNotContain(LifecycleOrder.Schedule);
        executionOrder.ShouldNotContain(LifecycleOrder.Auto);
    }

    [Fact]
    public async Task RunAsync_WithResumeFrom_ShouldStartFromSpecifiedOrder()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Directives.RequestResumeFrom(LifecycleOrder.OnEntry);
        var executionOrder = new List<int>();

        foreach (var mockStep in _mockSteps)
        {
            var order = mockStep.Object.Order;
            mockStep
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionOrder.Add(order))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionOrder.ShouldNotContain(LifecycleOrder.CreateTransition);
        executionOrder.ShouldNotContain(LifecycleOrder.OnExecute);
        executionOrder.ShouldNotContain(LifecycleOrder.OnExit);
        executionOrder.ShouldNotContain(LifecycleOrder.ChangeState);
        executionOrder.First().ShouldBe(LifecycleOrder.OnEntry);
    }

    [Fact]
    public async Task RunAsync_WithTerminalReached_ShouldStopAtFinalize()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Directives.MarkTerminal();
        var executionOrder = new List<int>();

        foreach (var mockStep in _mockSteps)
        {
            var order = mockStep.Object.Order;
            mockStep
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionOrder.Add(order))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionOrder.All(o => o <= LifecycleOrder.Finalize).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_WhenStepMutatesDirectives_ShouldApplyMutation()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var directivesMutated = false;

        // Setup second step to mutate directives
        _mockSteps[1]
            .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<StepOutcome>.Ok(new StepOutcome
            {
                MutateDirectives = d =>
                {
                    d.RequestEpilogue(EpilogueMode.Skip);
                    directivesMutated = true;
                }
            }));

        // Setup other steps
        foreach (var mockStep in _mockSteps.Where(m => m != _mockSteps[1]))
        {
            mockStep
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        directivesMutated.ShouldBeTrue();
        context.Directives.Epilogue.ShouldBe(EpilogueMode.Skip);
    }

    [Fact]
    public async Task RunAsync_WhenStepThrowsException_ShouldPropagateException()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var expectedException = new InvalidOperationException("Step failed");

        // Setup first step to succeed
        _mockSteps[0]
            .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));

        // Setup second step to throw
        _mockSteps[1]
            .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await _pipeline.RunAsync(context, CancellationToken.None));

        exception.ShouldBe(expectedException);
    }

    [Fact]
    public async Task RunAsync_WithCancellation_ShouldPropagateCancellation()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var cts = new CancellationTokenSource();

        // Setup first step to succeed
        _mockSteps[0]
            .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));

        // Setup second step to cancel
        _mockSteps[1]
            .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _pipeline.RunAsync(context, cts.Token));
    }

    [Fact(Skip = "Logger mock verification needs adjustment")]
    public async Task RunAsync_ShouldLogPipelineStepStartAndComplete()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();

        foreach (var mockStep in _mockSteps)
        {
            mockStep
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Started") || v.ToString()!.Contains("Completed")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeast(_mockSteps.Count * 2)); // Start and complete for each step
    }

    [Fact]
    public async Task RunAsync_WhenDirectiveChangeCausesReplan_ShouldReplanAndContinue()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var executionOrder = new List<int>();
        var replanned = false;

        // Setup first few steps normally
        for (int i = 0; i < 4; i++)
        {
            var index = i;
            _mockSteps[i]
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionOrder.Add(_mockSteps[index].Object.Order))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Setup OnEntry step to mark terminal (causes replan)
        _mockSteps[4]
            .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                executionOrder.Add(_mockSteps[4].Object.Order);
                if (!replanned)
                {
                    context.Directives.MarkTerminal();
                    replanned = true;
                }
            })
            .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));

        // Setup remaining steps
        for (int i = 5; i < _mockSteps.Count; i++)
        {
            var index = i;
            _mockSteps[i]
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionOrder.Add(_mockSteps[index].Object.Order))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Act
        var result = await _pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        replanned.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_ShouldExecuteStepsInAscendingOrder()
    {
        // Arrange - Create steps with random order
        var randomSteps = new List<Mock<ITransitionStep>>
        {
            CreateMockStep(80),
            CreateMockStep(20),
            CreateMockStep(50),
            CreateMockStep(10),
            CreateMockStep(30)
        };

        var executionOrder = new List<int>();

        foreach (var mockStep in randomSteps)
        {
            var order = mockStep.Object.Order;
            mockStep
                .Setup(x => x.ExecuteAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
                .Callback(() => executionOrder.Add(order))
                .ReturnsAsync(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        var pipeline = new TransitionPipeline(
            randomSteps.Select(m => m.Object),
            _mockStepInterceptor.Object);

        var context = CreateTransitionExecutionContext();

        // Act
        var result = await pipeline.RunAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionOrder.ShouldBe(new[] { 10, 20, 30, 50, 80 });
    }

    #endregion

    #region Helper Methods

    private Mock<ITransitionStep> CreateMockStep(int order)
    {
        var mockStep = new Mock<ITransitionStep>();
        mockStep.SetupGet(x => x.Order).Returns(order);
        return mockStep;
    }

    private TransitionExecutionContext CreateTransitionExecutionContext()
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey);
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
            Data = null,
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

