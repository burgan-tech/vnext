using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Unit tests for TransitionPipeline.
/// Validates request-scoped lock, reserved transition own lock, auto-chain under single lock,
/// busy marking, and fault handling.
/// </summary>
public class TransitionPipelineTests
{
    private readonly ILogger<TransitionPipeline> _mockLogger;
    private readonly ITransitionLockScopeFactory _mockLockScopeFactory;
    private readonly IReservedTransitionResolver _mockReservedResolver;
    private readonly IInstanceBusyManager _mockBusyMarker;
    private readonly ITransitionContextFactory _mockContextFactory;
    private readonly IPostCommitExecutor _mockPostCommitExecutor;
    private readonly IInstanceRepository _mockInstanceRepository;
    private readonly IUnitOfWorkManager _mockUowManager;
    private readonly ITransitionValidationService _mockValidationService;
    private readonly IStateNotificationScheduler _mockStateNotificationScheduler;
    private readonly List<ITransitionStep> _mockSteps;
    private readonly TransitionPipeline _pipeline;

    public TransitionPipelineTests()
    {
        _mockLogger = Substitute.For<ILogger<TransitionPipeline>>();
        _mockLockScopeFactory = Substitute.For<ITransitionLockScopeFactory>();
        _mockReservedResolver = Substitute.For<IReservedTransitionResolver>();
        _mockBusyMarker = Substitute.For<IInstanceBusyManager>();
        _mockContextFactory = Substitute.For<ITransitionContextFactory>();
        _mockPostCommitExecutor = Substitute.For<IPostCommitExecutor>();
        _mockInstanceRepository = Substitute.For<IInstanceRepository>();
        _mockUowManager = Substitute.For<IUnitOfWorkManager>();
        _mockValidationService = Substitute.For<ITransitionValidationService>();
        _mockStateNotificationScheduler = Substitute.For<IStateNotificationScheduler>();
        _mockSteps = new List<ITransitionStep>();

        _mockSteps.Add(CreateMockStep(LifecycleOrder.CreateTransition));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnExecute));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnExit));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.ChangeState));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnEntry));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Schedule));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Auto));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Finalize));

        // Default: lock acquired successfully
        var mockLockScope = CreateAcquiredLockScope();
        _mockLockScopeFactory
            .AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockLockScope);

        // Default: not a reserved transition
        _mockReservedResolver
            .IsReserved(Arg.Any<TransitionExecutionContext>())
            .Returns(false);

        // Default: own lock key = main lock key + ":reserved"
        _mockReservedResolver
            .GetOwnLockKey(Arg.Any<TransitionExecutionContext>())
            .Returns(ctx => ctx.ArgAt<TransitionExecutionContext>(0).LockKey + ":reserved");

        // Default: busy marker succeeds silently
        _mockBusyMarker
            .MarkBusyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Default: post-commit succeeds
        _mockPostCommitExecutor.ExecuteAsync(
            Arg.Any<IReadOnlyList<IPostCommitJob>>(),
            Arg.Any<TransitionExecutionContext>(),
            Arg.Any<CancellationToken>())
            .Returns(PostCommitResult.Ok());

        // Default: validation succeeds
        _mockValidationService.ValidateAsync(
            Arg.Any<TransitionExecutionContext>(),
            Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        _mockInstanceRepository
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<Instance>(0)));

        // The single-transition step runner now lives in TransitionExecutor (S2).
        // Wrap the mock steps in a real executor so pipeline behavior is preserved.
        var executor = new TransitionExecutor(
            _mockSteps,
            Substitute.For<ILogger<TransitionExecutor>>());

        // Continuation realization is now pluggable (S3); Inline reproduces the
        // original in-process auto-chain.
        var continuationDispatcher = new ContinuationDispatcher(
            new IContinuationStrategy[] { new InlineContinuationStrategy() });

        _pipeline = new TransitionPipeline(
            executor,
            continuationDispatcher,
            _mockLockScopeFactory,
            _mockReservedResolver,
            _mockBusyMarker,
            _mockContextFactory,
            _mockPostCommitExecutor,
            _mockInstanceRepository,
            _mockUowManager,
            _mockValidationService,
            new PipelineProfileResolver(),
            _mockStateNotificationScheduler,
            _mockLogger);
    }

    #region Lock Scope Tests

    [Fact]
    public async Task RunAsync_WithValidContext_ShouldAcquireLockOnceAndExecuteAllSteps()
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

        // Lock acquired exactly once for the entire chain
        await _mockLockScopeFactory.Received(1)
            .AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenLockFails_ShouldReturnConflictError()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);

        var failedScope = CreateNotAcquiredLockScope();
        _mockLockScopeFactory
            .AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(failedScope);

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConflictWorkflow);
    }

    [Fact]
    public async Task RunAsync_ShouldMarkBusyImmediatelyAfterLockAcquisition()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToSucceed();

        // Act
        await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        await _mockBusyMarker.Received(1)
            .MarkBusyAsync(context.InstanceId, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Reserved Transition Tests

    [Fact]
    public async Task RunAsync_WhenReservedTransition_ShouldAcquireOwnLockKeyNotMainLockKey()
    {
        // Arrange
        var context = CreateTransitionExecutionContext("cancel");
        var workflowContext = CreateWorkflowExecutionContext(context);
        var expectedOwnKey = context.LockKey + ":reserved";

        SetupContextFactory(context);
        SetupStepsToSucceed();

        _mockReservedResolver
            .IsReserved(Arg.Any<TransitionExecutionContext>())
            .Returns(true);

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();

        // Must acquire the own (type-specific) key, not the main flow lock key
        await _mockLockScopeFactory.Received(1)
            .AcquireAsync(expectedOwnKey, Arg.Any<CancellationToken>());

        await _mockLockScopeFactory.DidNotReceive()
            .AcquireAsync(context.LockKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenReservedNonResume_ShouldNotMarkBusy()
    {
        // Arrange
        var context = CreateTransitionExecutionContext("cancel");
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToSucceed();

        _mockReservedResolver
            .IsReserved(Arg.Any<TransitionExecutionContext>())
            .Returns(true);

        // Act
        await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert: busy NOT marked for non-subflow-resume reserved transitions
        await _mockBusyMarker.DidNotReceive()
            .MarkBusyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenSubFlowResume_ShouldAcquireResumeKeyAndMarkBusy()
    {
        // Arrange
        var context = CreateTransitionExecutionContext("resume");
        context.Directives.MarkAsSubFlowResume();
        var workflowContext = CreateWorkflowExecutionContext(context);
        var expectedOwnKey = context.LockKey + ":reserved"; // default mock returns :reserved

        SetupContextFactory(context);
        SetupStepsToSucceed();

        _mockReservedResolver
            .IsReserved(Arg.Any<TransitionExecutionContext>())
            .Returns(true);

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();

        await _mockLockScopeFactory.Received(1)
            .AcquireAsync(expectedOwnKey, Arg.Any<CancellationToken>());

        // Busy MUST be marked for subflow resume
        await _mockBusyMarker.Received(1)
            .MarkBusyAsync(context.InstanceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenReservedLockFails_ShouldReturnConflictError()
    {
        // Arrange
        var context = CreateTransitionExecutionContext("cancel");
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToSucceed();

        _mockReservedResolver
            .IsReserved(Arg.Any<TransitionExecutionContext>())
            .Returns(true);

        var failedScope = CreateNotAcquiredLockScope();
        _mockLockScopeFactory
            .AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(failedScope);

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConflictWorkflow);
    }

    #endregion

    #region Auto-Chain Tests

    [Fact]
    public async Task RunAsync_WithAutoChain_ShouldRunEntireChainUnderSingleLock()
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
                            ctx.Directives.RequestNextTransition(
                                new NextTransitionRequest("auto-transition", "auto"));
                            return Task.FromResult(
                                Result<StepOutcome>.Ok(StepOutcome.SkipTo(LifecycleOrder.Finalize)));
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
        contextCallCount.ShouldBe(2);

        // Lock acquired only ONCE for the entire chain (not per-transition)
        await _mockLockScopeFactory.Received(1)
            .AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WithAutoChain_ShouldExtendLockBetweenIterations()
    {
        // Arrange
        var context1 = CreateTransitionExecutionContext();
        var context2 = CreateTransitionExecutionContext("auto-transition");
        var workflowContext = CreateWorkflowExecutionContext(context1);
        var contextCallCount = 0;

        var mockLockScope = CreateAcquiredLockScope();

        _mockLockScopeFactory
            .AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockLockScope);

        _mockContextFactory.CreateAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                contextCallCount++;
                return Task.FromResult(
                    contextCallCount == 1
                        ? Result<TransitionExecutionContext>.Ok(context1)
                        : Result<TransitionExecutionContext>.Ok(context2));
            });

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
                            ctx.Directives.RequestNextTransition(
                                new NextTransitionRequest("auto-transition", "auto"));
                            return Task.FromResult(
                                Result<StepOutcome>.Ok(StepOutcome.SkipTo(LifecycleOrder.Finalize)));
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
        await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert: lock extended between chain iterations
        await mockLockScope.Received(1)
            .ExtendAsync(Arg.Any<CancellationToken>());
    }

    #endregion

    #region Fault Handling Tests

    [Fact]
    public async Task RunAsync_WhenStepFails_ShouldMarkInstanceFaultedWithSuccessResult()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);
        var error = Error.Failure("step.failed", "Step execution failed");
        var executionCount = 0;

        SetupContextFactory(context);

        for (int i = 0; i < 2; i++)
        {
            _mockSteps[i].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    executionCount++;
                    return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
                });
        }

        _mockSteps[2].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                executionCount++;
                return Task.FromResult(Result<StepOutcome>.Fail(error));
            });

        for (int i = 3; i < _mockSteps.Count; i++)
        {
            _mockSteps[i].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue())));
        }

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionCount.ShouldBe(3);
        await _mockInstanceRepository.Received(1)
            .UpdateAsync(
                Arg.Is<Instance>(x => x.Status.Equals(InstanceStatus.Faulted)),
                true,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenPostCommitFaults_ShouldRecordIncidentAndFaultInstance()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToSucceed();

        // Queue a post-commit job so the executor is invoked.
        context.Directives.EnqueuePostCommit(Substitute.For<IPostCommitJob>());

        // Simulate a subflow input-mapping failure that the failure policy classifies
        // as a system error (Instance:* prefix) -> ShouldMarkInstanceFaulted.
        var error = Error.Failure(
            "Instance:100023",
            "SubFlow 'credit-bureau-inquiry' input mapping failed: " +
            "'System.Dynamic.ExpandoObject' does not contain a definition for 'application'");

        _mockPostCommitExecutor.ExecuteAsync(
            Arg.Any<IReadOnlyList<IPostCommitJob>>(),
            Arg.Any<TransitionExecutionContext>(),
            Arg.Any<CancellationToken>())
            .Returns(PostCommitResult.Fail(
                error,
                new PostCommitFaultRequest(error.Code, error.Message, "   at SubFlowMapping.InputHandler()")));

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.HasActiveIncident.ShouldBeTrue();
        await _mockInstanceRepository.Received(1)
            .UpdateAsync(
                Arg.Is<Instance>(x => x.Status.Equals(InstanceStatus.Faulted) && x.HasActiveIncident),
                true,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenStepReturnsStop_ShouldStopPipeline()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);
        var executionCount = 0;

        SetupContextFactory(context);

        for (int i = 0; i < 2; i++)
        {
            _mockSteps[i].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    executionCount++;
                    return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
                });
        }

        _mockSteps[2].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                executionCount++;
                return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Stop()));
            });

        for (int i = 3; i < _mockSteps.Count; i++)
        {
            _mockSteps[i].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue())));
        }

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        executionCount.ShouldBe(3);
    }

    [Fact]
    public async Task RunAsync_WhenSkipImmediateExecution_ShouldNotAcquireLockOrExecuteSteps()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.SkipImmediateExecution = true;
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _mockLockScopeFactory.DidNotReceive()
            .AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region State Notification Tests

    [Fact]
    public async Task RunAsync_WhenSettledStateDeclaresNotification_ShouldScheduleStateNotification()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Target = CreateStateWithNotification("approved");
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToSucceed();

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert: settled (no next transition) + target declares a state notification -> scheduled once
        result.IsSuccess.ShouldBeTrue();
        await _mockStateNotificationScheduler.Received(1).ScheduleAsync(
            Arg.Is<TransitionExecutionContext>(c => c.Target!.Key == "approved"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenSettledStateHasNoNotification_ShouldNotScheduleStateNotification()
    {
        // Arrange: default target is null / declares no notification
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToSucceed();

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _mockStateNotificationScheduler.DidNotReceive().ScheduleAsync(
            Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WithAutoChain_ShouldScheduleOnlyForRestingState()
    {
        // Arrange: hop 1 lands on an intermediate state (with a notification) that auto-chains to a
        // second hop landing on the final resting state (also with a notification). Only the resting
        // hop should schedule — intermediate hops carry a next transition and never reach the hook.
        var context1 = CreateTransitionExecutionContext();
        context1.Target = CreateStateWithNotification("intermediate");
        var context2 = CreateTransitionExecutionContext("auto-transition");
        context2.Target = CreateStateWithNotification("final");
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
        await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert: scheduled once, for the resting state only
        await _mockStateNotificationScheduler.Received(1).ScheduleAsync(
            Arg.Is<TransitionExecutionContext>(c => c.Target!.Key == "final"),
            Arg.Any<CancellationToken>());
        await _mockStateNotificationScheduler.DidNotReceive().ScheduleAsync(
            Arg.Is<TransitionExecutionContext>(c => c.Target!.Key == "intermediate"),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Helper Methods

    private static State CreateStateWithNotification(string key)
    {
        // Uses JsonSerializerConstants.JsonOptions so the ScriptCodeJsonConverter is applied and the
        // mapping's Code is populated. The state declares a single 'state' notification entry.
        var json = $$"""
        {
            "key": "{{key}}",
            "stateType": "Intermediate",
            "subType": "None",
            "versionStrategy": "Patch",
            "notifications": [
                { "type": "state", "mapping": { "code": "Y29kZQ==", "encoding": "Base64" } }
            ]
        }
        """;

        return System.Text.Json.JsonSerializer.Deserialize<State>(json, JsonSerializerConstants.JsonOptions)!;
    }

    private ITransitionStep CreateMockStep(int order)
    {
        var mockStep = Substitute.For<ITransitionStep>();
        mockStep.Order.Returns(order);
        return mockStep;
    }

    private static ITransitionLockScope CreateAcquiredLockScope()
    {
        var scope = Substitute.For<ITransitionLockScope>();
        scope.IsAcquired.Returns(true);
        scope.LockKey.Returns("test-lock-key");
        scope.ExtendAsync(Arg.Any<CancellationToken>()).Returns(true);
        return scope;
    }

    private static ITransitionLockScope CreateNotAcquiredLockScope()
    {
        var scope = Substitute.For<ITransitionLockScope>();
        scope.IsAcquired.Returns(false);
        scope.LockKey.Returns("test-lock-key");
        return scope;
    }

    private void SetupContextFactory(TransitionExecutionContext context)
    {
        _mockContextFactory.CreateAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TransitionExecutionContext>.Ok(context)));
    }

    private void SetupStepsToSucceed()
    {
        foreach (var step in _mockSteps)
        {
            step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue())));
        }
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
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
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
