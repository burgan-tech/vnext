using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using BBT.Workflow.Logging;
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
    private readonly IInstanceBusyManager _mockBusyMarker;
    private readonly ITransitionContextFactory _mockContextFactory;
    private readonly IInstanceRepository _mockInstanceRepository;
    private readonly IInstanceJobRepository _mockInstanceJobRepository;
    private readonly IUnitOfWorkManager _mockUowManager;
    private readonly ITransitionValidationService _mockValidationService;
    private readonly IStateNotificationScheduler _mockStateNotificationScheduler;
    private readonly ITransitionAdmissionService _mockAdmissionService;
    private readonly IInstanceStatusLock _mockStatusLock;
    private readonly List<ITransitionStep> _mockSteps;
    private readonly TransitionPipeline _pipeline;

    public TransitionPipelineTests()
    {
        _mockLogger = Substitute.For<ILogger<TransitionPipeline>>();
        _mockBusyMarker = Substitute.For<IInstanceBusyManager>();
        _mockContextFactory = Substitute.For<ITransitionContextFactory>();
        _mockInstanceRepository = Substitute.For<IInstanceRepository>();
        _mockInstanceJobRepository = Substitute.For<IInstanceJobRepository>();
        // Default: no live transition jobs — a Busy without jobs reads as PARKED.
        _mockInstanceJobRepository
            .GetListActiveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<InstanceJob>());
        _mockUowManager = Substitute.For<IUnitOfWorkManager>();
        _mockValidationService = Substitute.For<ITransitionValidationService>();
        _mockStateNotificationScheduler = Substitute.For<IStateNotificationScheduler>();
        _mockAdmissionService = Substitute.For<ITransitionAdmissionService>();
        _mockAdmissionService.CheckAdmission(Arg.Any<TransitionExecutionContext>())
            .Returns(Result.Ok());
        // Default: Classify returns Normal (enum default); reserve and takeover succeed.
        _mockAdmissionService
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        _mockAdmissionService
            .TakeOverAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        _mockStatusLock = Substitute.For<IInstanceStatusLock>();
        _mockSteps = new List<ITransitionStep>();

        _mockSteps.Add(CreateMockStep(LifecycleOrder.CreateTransition));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnExecute));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.CancelScheduledJobs));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnExit));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.ChangeState));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.OnEntry));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Schedule));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Auto));
        _mockSteps.Add(CreateMockStep(LifecycleOrder.Finalize));

        // Default: busy marker flips the instance
        _mockBusyMarker
            .MarkBusyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Default: validation succeeds
        _mockValidationService.ValidateAsync(
            Arg.Any<TransitionExecutionContext>(),
            Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        // The pipeline itself runs policy-only validation per hop (schema is intake-only).
        _mockValidationService.ValidatePolicyAsync(
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
            _mockBusyMarker,
            _mockContextFactory,
            _mockInstanceRepository,
            _mockInstanceJobRepository,
            _mockUowManager,
            _mockValidationService,
            new PipelineProfileResolver(),
            _mockStateNotificationScheduler,
            _mockAdmissionService,
            _mockStatusLock,
            _mockLogger);
    }

    #region Lock Scope Tests

    [Fact]
    public async Task RunAsync_WithValidContext_ShouldReserveOnceAndExecuteAllSteps()
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

        // Admission reserve happens exactly once for the entire request
        await _mockAdmissionService.Received(1)
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// <c>updateData</c> writes data without moving the instance, so the state's lifecycle must not
    /// fire: OnEntry would re-run hooks for a state that was never re-entered and Schedule would
    /// re-arm the state's timers from zero. The transition's own work (OnExecute), the state sync
    /// (ChangeState) and the auto evaluation must still run — the whole point is to advance on the
    /// freshly written data.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenUpdateDataTargetsSelf_ShouldSkipStateLifecycleButStillEvaluateAutoTransitions()
    {
        // Arrange
        var context = CreateSelfTargetExecutionContext();
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

        executionOrder.ShouldNotContain(LifecycleOrder.CancelScheduledJobs);
        executionOrder.ShouldNotContain(LifecycleOrder.OnExit);
        executionOrder.ShouldNotContain(LifecycleOrder.OnEntry);
        executionOrder.ShouldNotContain(LifecycleOrder.Schedule);

        executionOrder.ShouldContain(LifecycleOrder.CreateTransition);
        executionOrder.ShouldContain(LifecycleOrder.OnExecute);
        executionOrder.ShouldContain(LifecycleOrder.ChangeState);
        executionOrder.ShouldContain(LifecycleOrder.Auto);
        executionOrder.ShouldContain(LifecycleOrder.Finalize);
    }

    /// <summary>
    /// The lifecycle skip is scoped to <c>updateData</c>, not to every <c>$self</c> target. A shared
    /// transition declaring <c>target: $self</c> is saying "do not move the instance", which is not
    /// the same instruction as "skip the state's hooks" — its state's OnExit/OnEntry must run and its
    /// scheduled transitions must be torn down and re-armed, exactly as before the self profile
    /// existed.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenASharedTransitionTargetsSelf_ShouldStillRunTheFullStateLifecycle()
    {
        // Arrange: $self target, but NOT updateData.
        var context = CreateSelfTargetExecutionContext(transitionKey: "share-mark");
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
        executionOrder.ShouldContain(LifecycleOrder.CancelScheduledJobs);
        executionOrder.ShouldContain(LifecycleOrder.OnExit);
        executionOrder.ShouldContain(LifecycleOrder.OnEntry);
        executionOrder.ShouldContain(LifecycleOrder.Schedule);
    }

    [Fact]
    public async Task RunAsync_WhenTransitionTargetsAnotherState_ShouldStillRunTheFullStateLifecycle()
    {
        // Arrange: the fixture's default transition targets state1 while the instance has not
        // entered it yet — a real state change, so nothing may be skipped.
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
        executionOrder.ShouldContain(LifecycleOrder.OnExit);
        executionOrder.ShouldContain(LifecycleOrder.OnEntry);
        executionOrder.ShouldContain(LifecycleOrder.Schedule);
    }

    /// <summary>
    /// Regression: the start transition is NOT a self transition, even though its target equals the
    /// instance's current state. InstanceCommandAppService pre-positions the instance into the
    /// initial state at creation (instance.ChangeState(initialState)) before dispatching the start
    /// transition, so target == currentState holds — but the state has not actually been entered
    /// yet, and entering it is precisely this transition's job. Treating it as a self target skips
    /// OnEntry (60) and the initial state's entry tasks never run at all.
    /// </summary>
    [Fact]
    public async Task RunAsync_ForTheStartTransition_ShouldRunOnEntryEvenThoughTargetEqualsCurrentState()
    {
        // Arrange
        var context = CreateStartTransitionExecutionContext();
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

        // Assert: the initial state must be entered like any other state.
        result.IsSuccess.ShouldBeTrue();
        executionOrder.ShouldContain(LifecycleOrder.OnEntry);
        executionOrder.ShouldContain(LifecycleOrder.Schedule);
    }

    /// <summary>
    /// Regression: retrying a transition that faulted AFTER the state change committed must still
    /// re-run OnEntry. ChangeStateStep persists with saveChanges, and MarkInstanceFaultedAsync
    /// reloads in a RequiresNew UoW — so a fault in OnEntry (60) leaves the instance committed in
    /// the target state. The retry then re-runs the same transition with target == currentState;
    /// treating that as a self target skips the very step that failed, and retry — whose whole
    /// purpose is re-running it, with already-succeeded tasks bypassed per record — becomes a no-op.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenRetryingAfterStateAlreadyCommitted_ShouldStillRunOnEntry()
    {
        // Arrange: instance already in the target state, same transition being retried.
        var context = CreateSelfTargetExecutionContext(
            transitionKey: "advance",
            target: "state1");
        context.RetryOfTransitionRecordId = Guid.NewGuid();

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
        executionOrder.ShouldContain(LifecycleOrder.OnEntry);
    }

    [Fact]
    public async Task RunAsync_WhenPostCommitJobsAreQueued_ShouldReturnThemWithoutExecutingOrConsumingThem()
    {
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);
        var postCommitJob = Substitute.For<IPostCommitJob>();
        var next = new NextTransitionRequest("next-transition", "automatic");

        SetupContextFactory(context);
        SetupStepsToSucceed();
        context.Target = CreateStateWithNotification("waiting-for-post-commit");
        context.Instance.Busy();
        context.Directives.RequestNextTransition(next);
        context.Directives.SetResolvedStatus(InstanceStatus.Active);
        context.Directives.EnqueuePostCommit(postCommitJob);

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Directives.PostCommitJobs.Count.ShouldBe(1);
        result.Value.Directives.PostCommitJobs.Single().ShouldBeSameAs(postCommitJob);
        result.Value.Directives.NextTransition.ShouldBeSameAs(next);
        result.Value.Directives.ResolvedStatus.ShouldBe(InstanceStatus.Active);
        await _mockInstanceRepository.DidNotReceive()
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _mockStateNotificationScheduler.DidNotReceive()
            .ScheduleAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenPostCommitJobsAreQueuedForEnqueueContinuation_ShouldDispatchBeforeReturning()
    {
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);
        workflowContext.EnqueueContinuations = true;
        var postCommitJob = Substitute.For<IPostCommitJob>();
        var next = new NextTransitionRequest("next-transition", "automatic");
        var jobRepository = Substitute.For<IInstanceJobRepository>();
        jobRepository.InsertAsync(
                Arg.Any<InstanceJob>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<InstanceJob>(0)));
        var enqueueGateway = Substitute.For<ITransitionEnqueueGateway>();
        enqueueGateway.EnqueueAsync(
                Arg.Any<BBT.Workflow.BackgroundJobs.Payloads.TransitionJobPayload>(),
                Arg.Any<BBT.Workflow.Execution.Events.TransitionContinuationRequested>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TransitionEnqueueOutcome(TransitionEnqueuePath.Direct)));
        var enqueueStrategy = new EnqueueContinuationStrategy(jobRepository, enqueueGateway);

        var jobsVisibleDuringEnqueue = false;
        enqueueGateway.EnqueueAsync(
                Arg.Any<BBT.Workflow.BackgroundJobs.Payloads.TransitionJobPayload>(),
                Arg.Any<BBT.Workflow.Execution.Events.TransitionContinuationRequested>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                jobsVisibleDuringEnqueue = context.Directives.PostCommitJobs.Single() == postCommitJob;
                return Task.FromResult(new TransitionEnqueueOutcome(TransitionEnqueuePath.Direct));
            });

        SetupContextFactory(context);
        SetupStepsToSucceed();
        context.Directives.RequestNextTransition(next);
        context.Directives.EnqueuePostCommit(postCommitJob);

        var pipeline = CreatePipelineWithContinuationStrategies(enqueueStrategy);

        var result = await pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        jobsVisibleDuringEnqueue.ShouldBeTrue();
        await jobRepository.Received(1).InsertAsync(
            Arg.Any<InstanceJob>(),
            true,
            Arg.Any<CancellationToken>());
        await enqueueGateway.Received(1).EnqueueAsync(
            Arg.Any<BBT.Workflow.BackgroundJobs.Payloads.TransitionJobPayload>(),
            Arg.Any<BBT.Workflow.Execution.Events.TransitionContinuationRequested>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        result.Value!.Directives.PostCommitJobs.Single().ShouldBeSameAs(postCommitJob);
        result.Value.Directives.NextTransition.ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_NormalTransition_ShouldReserveViaAdmissionNotBusyMarker()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToSucceed();

        // Act
        await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert: the short-lock reserve is the only admission-side status flip
        await _mockAdmissionService.Received(1)
            .ReserveAsync(context, Arg.Any<CancellationToken>());
        await _mockBusyMarker.DidNotReceive()
            .MarkBusyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion


    #region Auto-Chain Tests

    [Fact]
    public async Task RunAsync_WithAutoChain_ShouldAdmitOnlyOnceForTheWholeChain()
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

        // Admission runs only ONCE for the entire chain — hops carry no lock and no re-check
        await _mockAdmissionService.Received(1)
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The transaction span (job or HTTP request) names the FIRST transition, so that hop needs no
    /// span of its own — that is the whole point of dropping the old <c>transition/{key}</c> child.
    /// A chained hop is a different transition under the same transaction, so it gets a
    /// <c>Transition.{key}</c> group span; without one, two transitions' step spans would sit
    /// side by side under one parent with nothing to tell them apart.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithAutoChain_ShouldGroupOnlyTheChainedHopUnderATransitionSpan()
    {
        // Arrange — same 2-hop inline chain as the admission test above.
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

        var collected = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        contextCallCount.ShouldBe(2);

        collected.Count(a => a.DisplayName == "Transition.auto-transition").ShouldBe(1);
        collected.Any(a => a.DisplayName == "Transition.test-transition").ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_BusyAsMutex_WhenAdmissionRejects_ShouldReturnInstanceBusyWithoutSteps()
    {
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);
        var stepsExecuted = 0;

        SetupContextFactory(context);
        foreach (var step in _mockSteps)
        {
            step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    stepsExecuted++;
                    return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
                });
        }

        _mockAdmissionService.CheckAdmission(Arg.Any<TransitionExecutionContext>())
            .Returns(Result.Fail(WorkflowErrors.InstanceBusy(context.InstanceId, context.TransitionKey)));

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
        stepsExecuted.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_BypassKind_ShouldRunWithoutReserve()
    {
        var context = CreateTransitionExecutionContext("cancel");
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToContinue();

        _mockAdmissionService.Classify(Arg.Any<TransitionExecutionContext>())
            .Returns(AdmissionKind.BypassBusyCheck);

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        // Bypass kinds are exempt from the Busy 409 but still flip Busy under the short lock.
        await _mockAdmissionService.Received(1)
            .TakeOverAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
        await _mockAdmissionService.DidNotReceive()
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_BypassKindPreReserved_ShouldNotTakeOverAgain()
    {
        // The async accept already flipped Busy under its own status lock and the job re-enters
        // pre-reserved; taking the lock again here would put cancel back on two locks per request.
        var context = CreateTransitionExecutionContext("cancel");
        var workflowContext = CreateWorkflowExecutionContext(context);
        workflowContext.IsPreReserved = true;

        SetupContextFactory(context);
        SetupStepsToContinue();

        _mockAdmissionService.Classify(Arg.Any<TransitionExecutionContext>())
            .Returns(AdmissionKind.BypassBusyCheck);

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _mockAdmissionService.DidNotReceive()
            .TakeOverAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_BusyParentWithActiveSubflow_ShouldRunWithoutReserve()
    {
        // A Busy parent with an active SubFlow is admitted without a reserve — the chain runs
        // and ForwardToActiveSubflowStep relays the request to the subflow.
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToContinue();

        _mockAdmissionService.Classify(Arg.Any<TransitionExecutionContext>())
            .Returns(AdmissionKind.Normal);
        _mockAdmissionService.IsSubflowForward(Arg.Any<TransitionExecutionContext>())
            .Returns(true);

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _mockAdmissionService.DidNotReceive()
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
        await _mockAdmissionService.DidNotReceive()
            .TakeOverAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_UpdateData_WithoutContinuation_NeverOwnsStatusAndNeverReserves()
    {
        // updateData is status-neutral: it runs the full pipeline (data + tasks + auto
        // evaluation) but never reserves and never settles. With no satisfied auto
        // transition there is nothing to hand over, so no reserve happens at all.
        var context = CreateTransitionExecutionContext("update-parent-data");
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToContinue();

        _mockAdmissionService.Classify(Arg.Any<TransitionExecutionContext>())
            .Returns(AdmissionKind.Unconditional);

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.OwnsStatus.ShouldBeFalse();
        await _mockAdmissionService.DidNotReceive()
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
        await _mockAdmissionService.DidNotReceive()
            .TakeOverAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_UpdateData_SatisfiedAuto_ReservesAtContinuationBoundary()
    {
        // The satisfied auto transition must run as a real owner: the continuation boundary
        // reserves (Active→Busy) and the chained hop inherits the ownership.
        var updateDataContext = CreateTransitionExecutionContext("update-parent-data");
        var chainedContext = CreateTransitionExecutionContext("auto-transition");
        var workflowContext = CreateWorkflowExecutionContext(updateDataContext);

        SetupChainedContextFactory(updateDataContext, chainedContext);
        SetupAutoStepRequestingNextTransition("auto-transition");

        _mockAdmissionService.Classify(Arg.Any<TransitionExecutionContext>())
            .Returns(AdmissionKind.Unconditional);

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _mockAdmissionService.Received(1)
            .ReserveAsync(updateDataContext, Arg.Any<CancellationToken>());
        updateDataContext.OwnsStatus.ShouldBeTrue();
        chainedContext.OwnsStatus.ShouldBeTrue(); // carried over by the inline continuation
    }

    [Fact]
    public async Task RunAsync_UpdateData_BusyWithLiveOwner_DropsContinuation()
    {
        // A competing chain owns the instance (visible as an active transition job for a
        // DIFFERENT transition): the continuation is dropped (WARN) instead of advancing
        // ownerless — the owner is already progressing and re-evaluates with fresher data.
        var updateDataContext = CreateTransitionExecutionContext("update-parent-data");
        var chainedContext = CreateTransitionExecutionContext("auto-transition");
        var workflowContext = CreateWorkflowExecutionContext(updateDataContext);

        var contextCallCount = SetupChainedContextFactory(updateDataContext, chainedContext);
        SetupAutoStepRequestingNextTransition("auto-transition");

        _mockAdmissionService.Classify(Arg.Any<TransitionExecutionContext>())
            .Returns(AdmissionKind.Unconditional);
        _mockAdmissionService
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(WorkflowErrors.InstanceBusy(
                updateDataContext.InstanceId, updateDataContext.TransitionKey)));
        SetupLiveOwnerJob(updateDataContext.InstanceId, "some-running-transition");

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        updateDataContext.OwnsStatus.ShouldBeFalse();
        updateDataContext.Directives.NextTransition.ShouldBeNull(); // consumed and dropped
        contextCallCount().ShouldBe(1); // the chained hop never ran
        await _mockAdmissionService.DidNotReceive()
            .TakeOverAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
        await _mockInstanceRepository.DidNotReceive()
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_UpdateData_ParkedBusy_TakesOverAndContinues()
    {
        // The Busy has NO live owner (a fan-in wait state parks Busy at rest): the handoff
        // takes it over instead of dropping — otherwise the gate could never fire.
        var updateDataContext = CreateTransitionExecutionContext("update-parent-data");
        var chainedContext = CreateTransitionExecutionContext("auto-transition");
        var workflowContext = CreateWorkflowExecutionContext(updateDataContext);

        var contextCallCount = SetupChainedContextFactory(updateDataContext, chainedContext);
        SetupAutoStepRequestingNextTransition("auto-transition");

        _mockAdmissionService.Classify(Arg.Any<TransitionExecutionContext>())
            .Returns(AdmissionKind.Unconditional);
        _mockAdmissionService
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(WorkflowErrors.InstanceBusy(
                updateDataContext.InstanceId, updateDataContext.TransitionKey)));
        // Only this updateData's own accept intent is active — not a chain owner.
        SetupLiveOwnerJob(updateDataContext.InstanceId, "update-parent-data");

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _mockAdmissionService.Received(1)
            .TakeOverAsync(updateDataContext, Arg.Any<CancellationToken>());
        updateDataContext.OwnsStatus.ShouldBeTrue();
        chainedContext.OwnsStatus.ShouldBeTrue(); // the gate transition ran as a real owner
        contextCallCount().ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_OwnerReentry_ShouldRunWithoutReserve()
    {
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);

        SetupContextFactory(context);
        SetupStepsToContinue();

        _mockAdmissionService.Classify(Arg.Any<TransitionExecutionContext>())
            .Returns(AdmissionKind.OwnerReentry);

        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _mockAdmissionService.DidNotReceive()
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Stubs every fixture step to return Continue.
    /// </summary>
    private void SetupStepsToContinue()
    {
        foreach (var step in _mockSteps)
        {
            step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue())));
        }
    }

    /// <summary>
    /// Stubs the active-job probe with one active async-transition job targeting
    /// <paramref name="transitionKey"/> — a live owner when the key differs from the
    /// execution's own, this execution's own accept intent when it matches.
    /// </summary>
    private void SetupLiveOwnerJob(Guid instanceId, string transitionKey)
    {
        var jobId = Guid.NewGuid();
        var jobName = JobName.ForAsyncTransition(instanceId, "some-state", transitionKey, jobId);
        _mockInstanceJobRepository
            .GetListActiveAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceJob>
            {
                InstanceJob.Create(jobId, jobName, jobId, "test-domain", "test-flow", instanceId)
            });
    }

    /// <summary>
    /// Serves <paramref name="first"/> to the first context creation and <paramref name="chained"/>
    /// to every later one. Returns an accessor for the number of contexts created, so a test can
    /// assert whether the chained hop actually ran.
    /// </summary>
    private Func<int> SetupChainedContextFactory(
        TransitionExecutionContext first,
        TransitionExecutionContext chained)
    {
        var contextCallCount = 0;
        _mockContextFactory.CreateAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                contextCallCount++;
                return Task.FromResult(contextCallCount == 1
                    ? Result<TransitionExecutionContext>.Ok(first)
                    : Result<TransitionExecutionContext>.Ok(chained));
            });

        return () => contextCallCount;
    }

    /// <summary>
    /// Stubs the fixture steps so the Auto step requests <paramref name="nextTransitionKey"/> on
    /// its first invocation (as a satisfied auto condition does) and continues afterwards.
    /// </summary>
    private void SetupAutoStepRequestingNextTransition(string nextTransitionKey)
    {
        foreach (var step in _mockSteps)
        {
            if (step.Order != LifecycleOrder.Auto)
            {
                step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue())));
                continue;
            }

            var autoCallCount = 0;
            step.ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    autoCallCount++;
                    if (autoCallCount > 1)
                        return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));

                    callInfo.ArgAt<TransitionExecutionContext>(0).Directives
                        .RequestNextTransition(new NextTransitionRequest(nextTransitionKey, "auto"));
                    return Task.FromResult(
                        Result<StepOutcome>.Ok(StepOutcome.SkipTo(LifecycleOrder.Finalize)));
                });
        }
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
    public async Task RunAsync_WhenStepFailsWithClientFacingError_ShouldFaultInstanceAndReturnFailure()
    {
        // A caller-actionable failure (ResourceLockConflict) must fault the instance AND surface the
        // failure so the HTTP layer returns the mapped status (409) instead of "200 + Status=F".
        var context = CreateTransitionExecutionContext();
        var workflowContext = CreateWorkflowExecutionContext(context);
        var error = Error.Conflict(WorkflowErrorCodes.ResourceLockConflict, "Resource is already locked");

        SetupContextFactory(context);

        _mockSteps[0].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StepOutcome>.Fail(error)));
        for (int i = 1; i < _mockSteps.Count; i++)
        {
            _mockSteps[i].ExecuteAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue())));
        }

        // Act
        var result = await _pipeline.RunAsync(workflowContext, CancellationToken.None);

        // Assert: instance still faulted...
        await _mockInstanceRepository.Received(1)
            .UpdateAsync(
                Arg.Is<Instance>(x => x.Status.Equals(InstanceStatus.Faulted)),
                true,
                Arg.Any<CancellationToken>());
        // ...but the failure is propagated to the caller (→ 409), not swallowed into a success.
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ResourceLockConflict);
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
    public async Task RunAsync_WhenSkipImmediateExecution_ShouldNotReserveOrExecuteSteps()
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
        await _mockAdmissionService.DidNotReceive()
            .ReserveAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
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

    private TransitionPipeline CreatePipelineWithContinuationStrategies(
        params IContinuationStrategy[] continuationStrategies)
    {
        var executor = new TransitionExecutor(
            _mockSteps,
            Substitute.For<ILogger<TransitionExecutor>>());
        var continuationDispatcher = new ContinuationDispatcher(continuationStrategies);

        return new TransitionPipeline(
            executor,
            continuationDispatcher,
            _mockBusyMarker,
            _mockContextFactory,
            _mockInstanceRepository,
            _mockInstanceJobRepository,
            _mockUowManager,
            _mockValidationService,
            new PipelineProfileResolver(),
            _mockStateNotificationScheduler,
            _mockAdmissionService,
            _mockStatusLock,
            _mockLogger);
    }


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

    /// <summary>
    /// Builds a context whose instance is already in state1 and whose transition targets $self.
    /// <para>
    /// The default key is the reserved updateData alias ON PURPOSE: the self variant is composed for
    /// updateData alone, so a plain key here would silently resolve the base profile and any test
    /// asserting "the lifecycle was skipped" would pass while proving nothing.
    /// </para>
    /// </summary>
    private TransitionExecutionContext CreateSelfTargetExecutionContext(
        string transitionKey = WellKnownTransitionKeys.UpdateData,
        string? target = null)
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var state = workflow.GetState("state1").Value!;
        instance.ChangeState(state);

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
            Transition = Transition.Create(
                transitionKey,
                "state1",
                target ?? WellKnownStateKeys.Self,
                TriggerType.Manual,
                "Patch"),
            Instance = instance,
            Data = null,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    /// <summary>
    /// Reproduces the start path: the instance has already been stamped into the initial state by
    /// InstanceCommandAppService, and the start transition targets that same state by its literal
    /// key. Mirrors the fixture workflow's startTransition (key "start", target "state1").
    /// </summary>
    private TransitionExecutionContext CreateStartTransitionExecutionContext()
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var initialState = workflow.GetState("state1").Value!;
        instance.ChangeState(initialState);

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = workflow.StartTransition.Key,
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = initialState,
            Transition = workflow.StartTransition,
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
