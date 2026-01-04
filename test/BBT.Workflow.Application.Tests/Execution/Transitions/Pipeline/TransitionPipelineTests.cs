using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Unit tests for TransitionPipeline
/// Tests pipeline orchestration, step execution, lock management and flow control
/// </summary>
public class TransitionPipelineTests
{
    private readonly ILogger<TransitionPipeline> _mockLogger;
    private readonly IDistributedLockService _mockLockService;
    private readonly ITransitionContextFactory _mockContextFactory;
    private readonly IPostCommitExecutor _mockPostCommitExecutor;
    private readonly IInstanceRepository _mockInstanceRepository;
    private readonly List<ITransitionStep> _mockSteps;
    private readonly TransitionPipeline _pipeline;

    public TransitionPipelineTests()
    {
        _mockLogger = Substitute.For<ILogger<TransitionPipeline>>();
        _mockLockService = Substitute.For<IDistributedLockService>();
        _mockContextFactory = Substitute.For<ITransitionContextFactory>();
        _mockPostCommitExecutor = Substitute.For<IPostCommitExecutor>();
        _mockInstanceRepository = Substitute.For<IInstanceRepository>();
        _mockSteps = new List<ITransitionStep>();
        
        // Create a default set of steps in order
        _mockSteps.Add(CreateMockStep(LifecycleOrder.CreateTransition));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnExecute));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnExit));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.ChangeState));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnEntry));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Schedule));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Auto));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Finalize));

        // Default lock service behavior - always succeed
        _mockLockService.ExecuteWithLockAsync(
            Arg.Any<string>(),
            Arg.Any<Func<Task>>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var action = callInfo.ArgAt<Func<Task>>(1);
                action().GetAwaiter().GetResult();
                return true;
            });

        // Default post-commit executor behavior - always succeed
        _mockPostCommitExecutor.ExecuteAsync(
            Arg.Any<IReadOnlyList<IPostCommitJob>>(),
            Arg.Any<TransitionExecutionContext>(),
            Arg.Any<CancellationToken>())
            .Returns(PostCommitResult.Ok());

        _pipeline = new TransitionPipeline(
            _mockSteps,
            _mockLockService,
            _mockContextFactory,
            _mockPostCommitExecutor,
            _mockInstanceRepository,
            _mockLogger);
    }

    #region RunAsync Tests

    [Fact]
    public async Task RunAsync_WithValidContext_ShouldExecuteAllStepsInOrder()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);
        var executionOrder = new List<int>();

        SetupContextFactory(context);
        
        foreach (var step in _mockSteps)
        {
            var order = step.Order;
            step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    executionOrder.Add(order);
                    return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
                });
        }

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionOrder.Count.ShouldBe(_mockSteps.Count);
        executionOrder.ShouldBe(_mockSteps.Select(m => m.Order).OrderBy(o => o).ToList());
    }

    [Fact]
    public async Task RunAsync_WhenLockFails_ShouldReturnConflictError()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        
        _mockLockService.ExecuteWithLockAsync(
            Arg.Any<string>(),
            Arg.Any<Func<Task>>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConflictWorkflow);
    }

    [Fact]
    public async Task RunAsync_WhenStepFails_ShouldStopAndReturnError()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);
        var error = Error.Failure("step.failed", "Step execution failed");
        var executionCount = 0;

        SetupContextFactory(context);

        // Setup first two steps to succeed
        for (int i = 0; i < 2; i++)
        {
            _mockSteps[i].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    executionCount++;
                    return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
                });
        }

        // Setup third step to fail
        _mockSteps[2].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                executionCount++;
                return Task.FromResult(Result<StepOutcome>.Fail(error));
            });

        // Setup remaining steps
        for (int i = 3; i < _mockSteps.Count; i++)
        {
            _mockSteps[i].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue())));
        }

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

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
        var workflowContext = CreateWorkflowExecutionContext(context);
        var executionCount = 0;

        SetupContextFactory(context);

        // Setup first two steps to succeed
        for (int i = 0; i < 2; i++)
        {
            _mockSteps[i].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    executionCount++;
                    return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
                });
        }

        // Setup third step to stop pipeline
        _mockSteps[2].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                executionCount++;
                return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Stop()));
            });

        // Setup remaining steps
        for (int i = 3; i < _mockSteps.Count; i++)
        {
            _mockSteps[i].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue())));
        }

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionCount.ShouldBe(3); // Only first 3 steps executed
    }

    [Fact]
    public async Task RunAsync_WhenSkipImmediateExecution_ShouldNotExecuteSteps()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.SkipImmediateExecution = true;
        var workflowContext = CreateWorkflowExecutionContext(context);
        var executionCount = 0;

        SetupContextFactory(context);

        foreach (var step in _mockSteps)
        {
            step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    executionCount++;
                    return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
                });
        }

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionCount.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_WithNextTransitionRequest_ShouldChainTransitions()
    {
        // Arrange
        var context1 = CreateTransitionExecutionContext();
        var context2 = CreateTransitionExecutionContext("auto-transition");
        var workflowContext = CreateWorkflowExecutionContext(context1);
        var contextCallCount = 0;

        _mockContextFactory.CreateAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                contextCallCount++;
                return Task.FromResult(
                    contextCallCount == 1 
                        ? Result<TransitionExecutionContext>.Ok(context1)
                        : Result<TransitionExecutionContext>.Ok(context2));
            });

        // Setup steps to return continue except auto step which requests next transition
        foreach (var step in _mockSteps)
        {
            if (step.Order == LifecycleOrder.Auto)
            {
                var callCount = 0;
                step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        callCount++;
                        if (callCount == 1)
                        {
                            var ctx = callInfo.ArgAt<TransitionExecutionContext>(0);
                            ctx.Directives.RequestNextTransition(new NextTransitionRequest("auto-transition", "auto"));
                            return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.SkipTo(LifecycleOrder.Finalize)));
                        }
                        return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
                    });
            }
            else
            {
                step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue())));
            }
        }

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        contextCallCount.ShouldBe(2); // Two contexts created for two transitions
    }

    #endregion

    #region Helper Methods

    private ITransitionStep CreateMockStep(int order)
    {
        var mockStep = Substitute.For<ITransitionStep>();
        mockStep.Order.Returns(order);
        return mockStep;
    }

    private void SetupContextFactory(TransitionExecutionContext context)
    {
        _mockContextFactory.CreateAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TransitionExecutionContext>.Ok(context)));
    }

    private WorkflowExecutionContext CreateWorkflowExecutionContext(TransitionExecutionContext context)
    {
        return new WorkflowExecutionContext
        {
            Domain = context.Domain,
            InstanceId = context.InstanceId.ToString(),
            WorkflowKey = context.WorkflowKey,
            WorkflowVersion = context.Workflow.Version,
            TransitionKey = context.TransitionKey,
            TriggerType = TriggerType.Manual,
            Mode = ExecMode.Sync,
            Actor = ExecutionActor.User,
            CorrelationId = context.CorrelationId,
            RequestedAt = DateTimeOffset.UtcNow
        };
    }

    private TransitionExecutionContext CreateTransitionExecutionContext(string transitionKey = "test-transition")
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey);
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
