using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Application.Services;
using BBT.Aether.BackgroundJob;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Extentions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Headers;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for <see cref="InstanceCommandAppService.AcknowledgeLongPollAsync"/> — the long-poll
/// acknowledge that resumes a paused leaf or descends the active SubFlow chain to find it.
/// </summary>
public class InstanceCommandAppServiceLongPollAckTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Workflow = "test-flow";
    private const string Version = "1.0.0";
    private const string StateKey = "review";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly ITransitionAuthorizationManager _authManager = Substitute.For<ITransitionAuthorizationManager>();
    private readonly IInstanceCancellationService _cancellationService = Substitute.For<IInstanceCancellationService>();
    private readonly ILongPollAckResumeService _resumeService = Substitute.For<ILongPollAckResumeService>();
    private readonly IInstanceCommandGateway _gateway = Substitute.For<IInstanceCommandGateway>();
    private readonly IWorkflowContext _workflowContext = Substitute.For<IWorkflowContext>();
    private readonly IWorkflowExecutionService _workflowExecutionService = Substitute.For<IWorkflowExecutionService>();
    private readonly ITransitionValidationService _transitionValidationService = Substitute.For<ITransitionValidationService>();
    private readonly ITransitionContextFactory _transitionContextFactory = Substitute.For<ITransitionContextFactory>();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public InstanceCommandAppServiceLongPollAckTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        _resumeService.ResumeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        _gateway.AcknowledgeLongPollAsync(Arg.Any<AcknowledgeLongPollInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        _transitionValidationService
            .ValidateAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        _authManager
            .IsAnyRoleAllowedInStateAsync(
                Arg.Any<Definitions.Workflow>(),
                Arg.Any<Transition>(),
                Arg.Any<string?>(),
                Arg.Any<Instance>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<AuthorizationRequestContext>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _workflowExecutionService
            .ExecuteTransitionAsync(
                Arg.Any<WorkflowExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Status = InstanceStatus.Busy
            }));

        _service = new InstanceCommandAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            workflowExecutionService: _workflowExecutionService,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            backgroundJobService: Substitute.For<IBackgroundJobService>(),
            guidGenerator: Substitute.For<IGuidGenerator>(),
            headerService: Substitute.For<IHeaderService>(),
            transitionDataMapper: Substitute.For<ITransitionDataMapper>(),
            instanceDataMutationService: Substitute.For<IInstanceDataMutationService>(),
            transitionValidationService: _transitionValidationService,
            transitionContextFactory: _transitionContextFactory,
            reservedTransitionResolver: new ReservedTransitionResolver(),
            workflowContext: _workflowContext,
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            timerEvaluator: Substitute.For<ITimerEvaluator>(),
            transitionAuthorizationManager: _authManager,
            cancellationService: _cancellationService,
            longPollAckResumeService: _resumeService,
            instanceCommandGateway: _gateway,
            workflowOutputMappingService: Substitute.For<IWorkflowOutputMappingService>(),
            currentUser: Substitute.For<ICurrentUser>(),
            logger: Substitute.For<ILogger<InstanceCommandAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task AcknowledgeLongPollAsync_WhenInstanceAwaiting_ResumesLocally()
    {
        var instance = CreateInstance(awaiting: true, withSubflow: false);
        _instanceRepository.GetActiveAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));
        _componentCacheStore.GetFlowAsync(Domain, Workflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(BuildWorkflow()));

        var result = await _service.AcknowledgeLongPollAsync(Input(instance.Id.ToString()), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _resumeService.Received(1).ResumeAsync(Domain, Workflow, Version, instance.Id, Arg.Any<CancellationToken>());
        await _gateway.DidNotReceiveWithAnyArgs().AcknowledgeLongPollAsync(default!, default);
    }

    [Fact]
    public async Task AcknowledgeLongPollAsync_WhenNotAwaitingButHasSubflow_DescendsViaGateway()
    {
        var instance = CreateInstance(awaiting: false, withSubflow: true);
        _instanceRepository.GetActiveAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));

        var result = await _service.AcknowledgeLongPollAsync(Input(instance.Id.ToString()), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _gateway.Received(1).AcknowledgeLongPollAsync(
            Arg.Is<AcknowledgeLongPollInput>(i =>
                i.Domain == "sub-domain" && i.Workflow == "sub-flow" && i.Version == "1.0.0"),
            Arg.Any<CancellationToken>());
        await _resumeService.DidNotReceiveWithAnyArgs().ResumeAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task AcknowledgeLongPollAsync_WhenNotAwaitingAndNoSubflow_NoOp()
    {
        var instance = CreateInstance(awaiting: false, withSubflow: false);
        _instanceRepository.GetActiveAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));

        var result = await _service.AcknowledgeLongPollAsync(Input(instance.Id.ToString()), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _gateway.DidNotReceiveWithAnyArgs().AcknowledgeLongPollAsync(default!, default);
        await _resumeService.DidNotReceiveWithAnyArgs().ResumeAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task TransitionAsync_WhenNormalTransitionInstanceIsBusy_RejectsBeforeSchemaValidation()
    {
        var instance = CreateInstance(awaiting: false, withSubflow: false);
        instance.BeginChain(Guid.NewGuid());
        var workflow = BuildWorkflow();
        var transition = Transition.Create(
            "go",
            StateKey,
            StateKey,
            TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code);
        var transitionContext = new TransitionExecutionContext
        {
            Domain = Domain,
            WorkflowKey = Workflow,
            InstanceId = instance.Id,
            TransitionKey = transition.Key,
            Workflow = workflow,
            Instance = instance,
            Current = workflow.GetState(StateKey).Value!,
            Transition = transition
        };

        _instanceRepository.GetActiveAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));
        _componentCacheStore.GetFlowAsync(Domain, Workflow, Version, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
        _transitionContextFactory.CreateFromPreloaded(
                Arg.Any<WorkflowExecutionContext>(), workflow, instance)
            .Returns(Result<TransitionExecutionContext>.Ok(transitionContext));

        var result = await _service.TransitionAsync(
            instance.Id.ToString(),
            transition.Key,
            new TransitionInput(Domain, Workflow),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
        await _transitionValidationService.DidNotReceiveWithAnyArgs()
            .ValidateAsync(default!, default);
        await _workflowExecutionService.DidNotReceiveWithAnyArgs()
            .ExecuteTransitionAsync(default!, default);
    }

    [Fact]
    public async Task TransitionAsync_WhenBusyAsyncRequestHasIdempotencyKey_AllowsReplayResolution()
    {
        var instance = CreateInstance(awaiting: false, withSubflow: false);
        instance.BeginChain(Guid.NewGuid());
        var workflow = BuildWorkflow();
        var transition = Transition.Create(
            "go",
            StateKey,
            StateKey,
            TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code);
        var transitionContext = new TransitionExecutionContext
        {
            Domain = Domain,
            WorkflowKey = Workflow,
            InstanceId = instance.Id,
            TransitionKey = transition.Key,
            Workflow = workflow,
            Instance = instance,
            Current = workflow.GetState(StateKey).Value!,
            Transition = transition
        };

        _instanceRepository.GetActiveAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));
        _componentCacheStore.GetFlowAsync(Domain, Workflow, Version, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
        _transitionContextFactory.CreateFromPreloaded(
                Arg.Any<WorkflowExecutionContext>(), workflow, instance)
            .Returns(Result<TransitionExecutionContext>.Ok(transitionContext));
        var input = new TransitionInput(Domain, Workflow)
        {
            Headers = new Dictionary<string, string?>
            {
                ["Idempotency-Key"] = "retry-42"
            }
        };

        var result = await _service.TransitionAsync(
            instance.Id.ToString(),
            transition.Key,
            input,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _transitionValidationService.Received(1)
            .ValidateAsync(transitionContext, Arg.Any<CancellationToken>());
        await _workflowExecutionService.Received(1)
            .ExecuteTransitionAsync(
                Arg.Is<WorkflowExecutionContext>(context =>
                    context.Headers.ContainsKey("Idempotency-Key")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_WhenBusyParentHasActiveSubflow_AllowsPipelineForwarding()
    {
        var instance = CreateInstance(awaiting: false, withSubflow: true);
        instance.BeginChain(Guid.NewGuid());
        var workflow = BuildWorkflow();
        const string childTransition = "child-go";
        var transitionContext = new TransitionExecutionContext
        {
            Domain = Domain,
            WorkflowKey = Workflow,
            InstanceId = instance.Id,
            TransitionKey = childTransition,
            Workflow = workflow,
            Instance = instance,
            Current = workflow.GetState(StateKey).Value!,
            Transition = null
        };

        _instanceRepository.GetActiveAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));
        _componentCacheStore.GetFlowAsync(Domain, Workflow, Version, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
        _transitionContextFactory.CreateFromPreloaded(
                Arg.Any<WorkflowExecutionContext>(), workflow, instance)
            .Returns(Result<TransitionExecutionContext>.Ok(transitionContext));

        var result = await _service.TransitionAsync(
            instance.Id.ToString(),
            childTransition,
            new TransitionInput(Domain, Workflow),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _transitionValidationService.Received(1)
            .ValidateAsync(transitionContext, Arg.Any<CancellationToken>());
        await _workflowExecutionService.Received(1)
            .ExecuteTransitionAsync(
                Arg.Is<WorkflowExecutionContext>(context => context.TransitionKey == childTransition),
                Arg.Any<CancellationToken>());
    }

    private static Instance CreateInstance(bool awaiting, bool withSubflow)
    {
        var instance = Instance.Create(Guid.NewGuid(), Workflow, Version, "test-key");
        instance.ChangeState(State.Create(StateKey, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreasePatch.Code));
        if (withSubflow)
        {
            instance.AddCorrelation(InstanceCorrelation.Create(
                Guid.NewGuid(), instance.Id, StateKey, Guid.NewGuid(),
                "S", "sub-domain", "sub-flow", "1.0.0"));
        }
        if (awaiting)
            instance.ArmLongPollAck(Guid.NewGuid());
        return instance;
    }

    private static Definitions.Workflow BuildWorkflow()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(Workflow, Domain, "sys-flows", Version));
        workflow.SetType("F");
        var state = State.Create(StateKey, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreasePatch.Code);
        workflow.SetStartTransition(Transition.Create("start", null, StateKey, TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        workflow.AddState(state);
        return workflow;
    }

    private static AcknowledgeLongPollInput Input(string instanceId) => new()
    {
        Domain = Domain,
        Workflow = Workflow,
        Instance = instanceId,
        Version = Version,
        Headers = new Dictionary<string, string?>()
    };
}
