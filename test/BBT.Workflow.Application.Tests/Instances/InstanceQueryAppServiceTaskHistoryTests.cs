using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Extentions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for the task-history and action-history system function reads
/// (<see cref="InstanceQueryAppService.GetInstanceTasksAsync"/> /
/// <see cref="InstanceQueryAppService.GetInstanceTaskActionsAsync"/>). Pins the queryRoles gate,
/// the metadata-only DTO projection (fault reason extracted from the conditionally fetched
/// Response, payloads absent by construction) and the instance-scoping of the action read.
/// </summary>
public class InstanceQueryAppServiceTaskHistoryTests : IDisposable
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string TestState = "waiting";

    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly IComponentCacheStore _componentCacheStore;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IInstanceTaskRepository _instanceTaskRepository;
    private readonly IInstanceActionRepository _instanceActionRepository;
    private readonly ITransitionAuthorizationManager _transitionAuthorizationManager;
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceQueryAppServiceTaskHistoryTests()
    {
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _instanceTaskRepository = Substitute.For<IInstanceTaskRepository>();
        _instanceActionRepository = Substitute.For<IInstanceActionRepository>();
        _transitionAuthorizationManager = Substitute.For<ITransitionAuthorizationManager>();

        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager
            .BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));

        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        _ambientServiceProvider = services.BuildServiceProvider();

        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        _service = new InstanceQueryAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: _runtimeInfoProvider,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceTransitionRepository: Substitute.For<IInstanceTransitionRepository>(),
            instanceCorrelationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            instanceIncidentRepository: Substitute.For<IInstanceIncidentRepository>(),
            instanceTaskRepository: _instanceTaskRepository,
            instanceActionRepository: _instanceActionRepository,
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: Substitute.For<IUrlTemplateBuilder>(),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: _transitionAuthorizationManager,
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            callerRoleResolver: new DefaultCallerRoleResolver(Substitute.For<ICurrentUser>()),
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task GetInstanceTasksAsync_WhenInstanceNotFound_ReturnsFailure()
    {
        var input = TasksInput(Guid.NewGuid().ToString());

        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(input.Instance, Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await _service.GetInstanceTasksAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        await _instanceTaskRepository.DidNotReceive().GetHistoryByInstanceIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceTasksAsync_WhenQueryRolesDeny_ReturnsForbiddenWithoutReading()
    {
        var instance = SetupInstance(queryAllowed: false);
        var input = TasksInput(instance.Id.ToString());

        var result = await _service.GetInstanceTasksAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
        await _instanceTaskRepository.DidNotReceive().GetHistoryByInstanceIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceTasksAsync_MapsRowsToMetadataDtos()
    {
        var instance = SetupInstance();
        var startedAt = DateTime.UtcNow.AddSeconds(-5);

        _instanceTaskRepository
            .GetHistoryByInstanceIdAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(
            [
                new InstanceTaskHistoryRow(
                    Guid.NewGuid(), "send-mail", "start", "initial", "draft", TriggerType.Manual,
                    Definitions.TaskStatus.Completed, BusinessStatus.Success,
                    startedAt, startedAt.AddMilliseconds(55), TimeSpan.FromMilliseconds(55),
                    FaultedResponseJson: null),
                new InstanceTaskHistoryRow(
                    Guid.NewGuid(), "call-api", "approve", "draft", "approved", TriggerType.Automatic,
                    Definitions.TaskStatus.Faulted, BusinessStatus.Unknown,
                    startedAt.AddSeconds(1), startedAt.AddSeconds(2), TimeSpan.FromSeconds(1),
                    FaultedResponseJson: """{"error":"connection refused"}""")
            ]);

        var result = await _service.GetInstanceTasksAsync(TasksInput(instance.Id.ToString()), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var output = result.Value!;
        output.Items.Count.ShouldBe(2);

        var completed = output.Items[0];
        completed.TaskKey.ShouldBe("send-mail");
        completed.TransitionKey.ShouldBe("start");
        completed.FromState.ShouldBe("initial");
        completed.ToState.ShouldBe("draft");
        completed.TriggerType.ShouldBe(TriggerType.Manual);
        completed.Status.ShouldBe(Definitions.TaskStatus.Completed);
        completed.BusinessStatus.ShouldBe(BusinessStatus.Success);
        completed.DurationMs.ShouldBe(55);
        completed.Error.ShouldBeNull();

        var faulted = output.Items[1];
        faulted.Status.ShouldBe(Definitions.TaskStatus.Faulted);
        faulted.BusinessStatus.ShouldBe(BusinessStatus.Unknown);
        faulted.Error.ShouldBe("connection refused");
    }

    [Fact]
    public async Task GetInstanceTaskActionsAsync_WhenTaskNotInInstance_ReturnsNotFound()
    {
        var instance = SetupInstance();
        var taskId = Guid.NewGuid();

        _instanceTaskRepository
            .GetRefForInstanceAsync(instance.Id, taskId, Arg.Any<CancellationToken>())
            .Returns((InstanceTaskRef?)null);

        var result = await _service.GetInstanceTaskActionsAsync(
            ActionsInput(instance.Id.ToString(), taskId), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceTaskNotFound);
        await _instanceActionRepository.DidNotReceive().GetByTaskIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceTaskActionsAsync_WhenQueryRolesDeny_ReturnsForbidden()
    {
        var instance = SetupInstance(queryAllowed: false);

        var result = await _service.GetInstanceTaskActionsAsync(
            ActionsInput(instance.Id.ToString(), Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
        await _instanceTaskRepository.DidNotReceive().GetRefForInstanceAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceTaskActionsAsync_MapsActionsWithOwningTaskContext()
    {
        var instance = SetupInstance();
        var taskId = Guid.NewGuid();

        _instanceTaskRepository
            .GetRefForInstanceAsync(instance.Id, taskId, Arg.Any<CancellationToken>())
            .Returns(new InstanceTaskRef(taskId, "call-api"));

        var action = new InstanceAction(
            Guid.NewGuid(), taskId, "invoke", JsonData.CreateFrom("""{"attempt":1}"""));
        _instanceActionRepository
            .GetByTaskIdAsync(taskId, Arg.Any<CancellationToken>())
            .Returns([action]);

        var result = await _service.GetInstanceTaskActionsAsync(
            ActionsInput(instance.Id.ToString(), taskId), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var output = result.Value!;
        output.TaskId.ShouldBe(taskId);
        output.TaskKey.ShouldBe("call-api");
        output.Items.Count.ShouldBe(1);
        output.Items[0].Id.ShouldBe(action.Id);
        output.Items[0].Status.ShouldBe("invoke");
        output.Items[0].Detail.ShouldNotBeNull();
        output.Items[0].Detail!.Value.GetProperty("attempt").GetInt32().ShouldBe(1);
    }

    private Instance SetupInstance(bool queryAllowed = true)
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));
        workflow.SetType("F");
        workflow.SetStartTransition(Transition.Create("start", null, state.Key, TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        workflow.AddState(state);

        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);

        _componentCacheStore
            .GetFlowAsync(TestDomain, TestWorkflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));

        _transitionAuthorizationManager
            .IsQueryAllowedAsync(
                Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(),
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(queryAllowed);

        return instance;
    }

    private static GetInstanceTasksInput TasksInput(string instance) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instance
    };

    private static GetInstanceTaskActionsInput ActionsInput(string instance, Guid taskId) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instance,
        TaskId = taskId
    };
}
