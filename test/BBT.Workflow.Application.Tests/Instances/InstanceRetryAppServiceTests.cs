using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

/// <summary>
/// Drives <see cref="InstanceRetryAppService"/> end-to-end (through its real <c>ApplicationService</c>
/// / <see cref="IUnitOfWorkManager"/> plumbing, with every collaborator mocked) to pin the ONE
/// invariant the subflow-restart branch must never violate: once the parent has been re-armed Busy
/// for the restart, every exit either succeeds (the child now exists) or re-faults the parent —
/// including a THROWN exception, not just a returned <see cref="Result"/> failure. Each test below
/// corresponds to one row of that per-exit table.
/// <para>
/// The "child is missing" probe result and the fresh-scope handler/context-factory/mutation-service
/// are all mocked; <c>SubflowStartFailureLabTests</c> in the vnext-example integration suite is what
/// proves the real <c>StartSubflowJobHandler</c>/<c>TransitionContextFactory</c> behave the same way
/// against a running runtime.
/// </para>
/// </summary>
public sealed class InstanceRetryAppServiceRestartBranchTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "subflow-start-failure-parent";
    private const string FlowVersion = "1.0.0";
    private const string ParentState = "parent-subflow-state";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceIncidentRepository _instanceIncidentRepository = Substitute.For<IInstanceIncidentRepository>();
    private readonly IInstanceTransitionRepository _instanceTransitionRepository = Substitute.For<IInstanceTransitionRepository>();
    private readonly IInstanceQueryGateway _instanceQueryGateway = Substitute.For<IInstanceQueryGateway>();
    private readonly ICallerRoleResolver _callerRoleResolver = Substitute.For<ICallerRoleResolver>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceStatusLock _instanceStatusLock = Substitute.For<IInstanceStatusLock>();
    private readonly ITransitionLockScope _lockScope = Substitute.For<ITransitionLockScope>();

    // The "fresh DI scope" collaborators — resolved by RestartMissingSubflowChildAsync via
    // IServiceScopeFactory.ExecuteWithWorkflowAsync, never via this app service's own constructor
    // injection (that is the whole point of the fresh-scope fix).
    private readonly ITransitionContextFactory _freshContextFactory = Substitute.For<ITransitionContextFactory>();
    private readonly IPostCommitHandler<StartSubflowJob> _freshStartSubflowJobHandler = Substitute.For<IPostCommitHandler<StartSubflowJob>>();
    private readonly IPostCommitParentMutationService _freshMutationService = Substitute.For<IPostCommitParentMutationService>();

    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;
    private readonly InstanceRetryAppService _service;
    private readonly InstanceCorrelation _correlation;
    private readonly Instance _instance;
    private readonly Guid _childInstanceId = Guid.NewGuid();
    private readonly Definitions.Workflow _workflow = WorkflowFactory.CreateDefault(Flow, Domain, FlowVersion);

    public InstanceRetryAppServiceRestartBranchTests()
    {
        // Ambient scope: only what ApplicationService.UnitOfWorkManager needs to resolve, for the
        // unfault/Busy CAS block, which runs in THIS app service's own ambient unit of work (a
        // set-based CAS, never a tracked load — see RestartMissingSubflowChildAsync's own remarks on
        // why that part is safe as-is).
        var mockUow = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(mockUow);

        var ambientServices = new ServiceCollection();
        ambientServices.AddSingleton(mockUoWManager);
        ambientServices.AddSingleton<ILazyServiceProvider>(sp => new LazyServiceProvider(sp));
        _ambient = ambientServices.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        // Fresh scope: what RunSubflowRestartAsync / FaultParentAfterFailedRestartAsync resolve
        // from IServiceScopeFactory.ExecuteWithWorkflowAsync, entirely separate from the ambient one.
        var freshServices = new ServiceCollection();
        var currentSchema = Substitute.For<BBT.Aether.MultiSchema.ICurrentSchema>();
        currentSchema.Change(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());
        freshServices.AddSingleton(currentSchema);
        freshServices.AddSingleton(_componentCacheStore);
        freshServices.AddSingleton(_freshContextFactory);
        freshServices.AddSingleton(_freshStartSubflowJobHandler);
        freshServices.AddSingleton(_freshMutationService);
        var freshProvider = freshServices.BuildServiceProvider();

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(_ => new FakeServiceScope(freshProvider));

        _instanceStatusLock.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_lockScope));
        _lockScope.IsAcquired.Returns(true);

        _componentCacheStore.GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(_workflow));

        _callerRoleResolver.ResolveRolesAsync(Arg.Any<Dictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(null));

        // The probe finds the child genuinely missing — the ONLY way RestartMissingSubflowChildAsync
        // is reached at all.
        _instanceQueryGateway.GetFunctionWithStateAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConditionalResult<GetInstanceStateOutput>.Fail(
                WorkflowErrors.InstanceNotFound(_childInstanceId.ToString()))));

        _instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "parent-key");
        _instance.ChangeState(State.Create(ParentState, StateType.SubFlow, StateSubType.None, "Minor"));
        _correlation = InstanceCorrelation.Create(
            Guid.NewGuid(), _instance.Id, ParentState, _childInstanceId, "S", Domain, "child-flow", "1.0.0");
        _instance.AddCorrelation(_correlation);
        _instance.Fault(Domain);

        _instanceRepository.GetResultAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(_instance));
        _instanceRepository.TryUnfaultAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>()).Returns(true);
        _instanceRepository.TryMarkBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>()).Returns(true);
        _instanceIncidentRepository.ResolveAllAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var originatingTransition = new InstanceTransitionSlim(
            Guid.NewGuid(), _instance.Id, "auto-to-subflow", "parent-initial", ParentState,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(-1).AddSeconds(1), null,
            TriggerType.Automatic, DateTime.UtcNow.AddMinutes(-1), null, null);
        _instanceTransitionRepository.GetByInstanceIdAsReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransitionSlim> { originatingTransition });

        _service = new InstanceRetryAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            instanceRepository: _instanceRepository,
            instanceIncidentRepository: _instanceIncidentRepository,
            instanceTransitionRepository: _instanceTransitionRepository,
            instanceQueryGateway: _instanceQueryGateway,
            instanceRetryGateway: Substitute.For<IInstanceRetryGateway>(),
            componentCacheStore: _componentCacheStore,
            workflowExecutionService: Substitute.For<IWorkflowExecutionService>(),
            callerRoleResolver: _callerRoleResolver,
            scopeFactory: scopeFactory,
            instanceStatusLock: _instanceStatusLock,
            logger: Substitute.For<ILogger<InstanceRetryAppService>>());
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbient;

    private RetryInstanceInput CreateInput() => new()
    {
        Domain = Domain,
        Workflow = Flow,
        Instance = _instance.Id.ToString(),
        Sync = true,
        Headers = new Dictionary<string, string?>(),
        RouteValues = new Dictionary<string, string?>()
    };

    private TransitionExecutionContext CreateFreshTransitionContext(Instance freshInstance) => new()
    {
        Domain = Domain,
        InstanceId = freshInstance.Id,
        WorkflowKey = Flow,
        TransitionKey = "auto-to-subflow",
        CorrelationId = Guid.NewGuid().ToString("N"),
        ExecutionChainId = Guid.NewGuid().ToString("N"),
        Workflow = _workflow,
        Current = State.Create(ParentState, StateType.SubFlow, StateSubType.None, "Minor"),
        Transition = TransitionFactory.CreateDefault(),
        Instance = freshInstance
    };

    /// <summary>Exit 1 of the table: the restart succeeds. Busy is re-armed, no fault, no exception.</summary>
    [Fact]
    public async Task RestartSucceeds_ReArmsBusy_ReturnsSuccess_AndNeverFaultsTheParent()
    {
        var freshInstance = Instance.Create(_instance.Id, Flow, FlowVersion, "parent-key");
        freshInstance.ChangeState(State.Create(ParentState, StateType.SubFlow, StateSubType.None, "Minor"));
        // Busy is what a freshly reloaded, still-waiting-on-a-live-child parent looks like.
        typeof(Instance).GetMethod("Busy")!.Invoke(freshInstance, null);

        _freshContextFactory
            .CreateAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionExecutionContext>.Ok(CreateFreshTransitionContext(freshInstance)));
        _freshStartSubflowJobHandler
            .HandleAsync(Arg.Any<StartSubflowJob>(), Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        var result = await _service.RetryAsync(CreateInput());

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(InstanceStatus.Busy);

        await _instanceRepository.Received(1).TryMarkBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
        await _freshMutationService.DidNotReceive().FaultAsync(
            Arg.Any<PostCommitParentSnapshot>(), Arg.Any<PostCommitFaultRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Exit 2: the handler returns Result.Fail (e.g. the child's schema validation failed again).
    /// The parent must be re-faulted with THIS error, and this error — not the fault call's own — is
    /// what the caller sees.
    /// </summary>
    [Fact]
    public async Task RestartFails_FaultsTheParent_AndReturnsTheOriginalError()
    {
        var restartError = Error.Validation("Task:400011", "child schema validation failed again");

        _freshContextFactory
            .CreateAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionExecutionContext>.Ok(CreateFreshTransitionContext(_instance)));
        _freshStartSubflowJobHandler
            .HandleAsync(Arg.Any<StartSubflowJob>(), Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(restartError));
        _freshMutationService
            .FaultAsync(Arg.Any<PostCommitParentSnapshot>(), Arg.Any<PostCommitFaultRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = _instance.Id, Status = InstanceStatus.Faulted }));

        var result = await _service.RetryAsync(CreateInput());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(restartError.Code);

        await _freshMutationService.Received(1).FaultAsync(
            Arg.Any<PostCommitParentSnapshot>(),
            Arg.Is<PostCommitFaultRequest>(r => r.ErrorCode == restartError.Code),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Exit 3 — the Critical #1 fix: the handler THROWS instead of returning Result.Fail. Before this
    /// fix, that exception escaped RestartMissingSubflowChildAsync entirely, past the Busy re-arm,
    /// leaving the parent Busy with no incident and (per RetryAsync's own routing) no way back into
    /// this branch. It must instead be caught, converted, and treated exactly like a returned failure.
    /// </summary>
    [Fact]
    public async Task RestartThrows_IsCaughtNotEscaped_AndFaultsTheParent()
    {
        _freshContextFactory
            .CreateAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionExecutionContext>.Ok(CreateFreshTransitionContext(_instance)));
        _freshStartSubflowJobHandler
            .HandleAsync(Arg.Any<StartSubflowJob>(), Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("boom — mapping blew up"));
        _freshMutationService
            .FaultAsync(Arg.Any<PostCommitParentSnapshot>(), Arg.Any<PostCommitFaultRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = _instance.Id, Status = InstanceStatus.Faulted }));

        // The defining assertion: this must complete as a Result, never propagate the exception.
        var result = await _service.RetryAsync(CreateInput());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("boom");

        await _freshMutationService.Received(1).FaultAsync(
            Arg.Any<PostCommitParentSnapshot>(), Arg.Any<PostCommitFaultRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Exit 4 — the Critical #2 residual case: the restart fails AND every bounded attempt to
    /// re-fault the parent also fails (e.g. the status lock stays contended). This is the one shape
    /// the fix cannot fully guarantee end-to-end against a live runtime (forcing a real, persistent
    /// lock conflict is impractical), so it is pinned here instead: the ORIGINAL restart error is
    /// still what the caller sees (never masked by the compensation failure), the compensation is
    /// retried up to the bound and not once more, and nothing throws.
    /// </summary>
    [Fact]
    public async Task RestartFails_AndCompensationAlsoExhausts_StillReturnsOriginalError_WithoutThrowing()
    {
        var restartError = Error.Failure("Task:400011", "child start failed");

        _freshContextFactory
            .CreateAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionExecutionContext>.Ok(CreateFreshTransitionContext(_instance)));
        _freshStartSubflowJobHandler
            .HandleAsync(Arg.Any<StartSubflowJob>(), Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(restartError));
        _freshMutationService
            .FaultAsync(Arg.Any<PostCommitParentSnapshot>(), Arg.Any<PostCommitFaultRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Fail(Error.Conflict("App:900001", "lock conflict")));

        var result = await _service.RetryAsync(CreateInput());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(restartError.Code);

        // Bounded — exactly the configured number of attempts, not unbounded and not just one.
        await _freshMutationService.Received(3).FaultAsync(
            Arg.Any<PostCommitParentSnapshot>(), Arg.Any<PostCommitFaultRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A trivial <see cref="IServiceScope"/> wrapping an already-built provider.</summary>
    private sealed class FakeServiceScope(IServiceProvider serviceProvider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public void Dispose() { }
    }
}
