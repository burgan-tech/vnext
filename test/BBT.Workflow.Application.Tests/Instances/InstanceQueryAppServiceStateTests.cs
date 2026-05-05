using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.DTOs;
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
/// Unit tests for InstanceQueryAppService.GetInstanceStateAsync.
/// Focuses on the propagation window race condition fix:
/// when a SubFlow reports a terminal status but the parent correlation is not yet marked
/// as completed (IsCompleted=false), the parent must return its own Busy status
/// instead of the SubFlow's terminal status.
/// </summary>
public class InstanceQueryAppServiceStateTests : IDisposable
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly IComponentCacheStore _componentCacheStore;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IInstanceQueryGateway _instanceQueryGateway;
    private readonly IRepresentationEtagService _representationEtagService;
    private readonly IUrlTemplateBuilder _urlTemplateBuilder;
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string TestState = "review";

    public InstanceQueryAppServiceStateTests()
    {
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _instanceQueryGateway = Substitute.For<IInstanceQueryGateway>();
        _representationEtagService = Substitute.For<IRepresentationEtagService>();
        _urlTemplateBuilder = Substitute.For<IUrlTemplateBuilder>();

        // Set up AmbientServiceProvider.Current needed by PostSharp UnitOfWorkAttribute
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
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            instanceQueryGateway: _instanceQueryGateway,
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: _urlTemplateBuilder,
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: _representationEtagService,
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Verifies the propagation window guard:
    /// when the SubFlow reports Completed but the parent correlation IsCompleted=false,
    /// the parent returns its own Busy status — not the SubFlow's Completed status.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenSubFlowCompletedButCorrelationStillActive_ReturnsBusy()
    {
        // Arrange
        var (instance, workflow) = CreateParentWithActiveSubFlow();
        SetupCommonMocks(instance, workflow);
        SetupSubFlowGateway(InstanceStatus.Completed, "done");

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.IsNotModified.ShouldBeFalse();
        result.Result.IsSuccess.ShouldBeTrue();

        var output = result.Result.Value!;
        output.Status.ShouldBe(InstanceStatus.Busy);
        output.State.ShouldBe(TestState);
        output.Transitions.ShouldBeEmpty();
    }

    /// <summary>
    /// Same guard applies when SubFlow is Faulted — parent returns its own Busy status.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenSubFlowFaultedButCorrelationStillActive_ReturnsBusy()
    {
        // Arrange
        var (instance, workflow) = CreateParentWithActiveSubFlow();
        SetupCommonMocks(instance, workflow);
        SetupSubFlowGateway(InstanceStatus.Faulted, "error-state");

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Status.ShouldBe(InstanceStatus.Busy);
        result.Result.Value!.Transitions.ShouldBeEmpty();
    }

    /// <summary>
    /// Same guard applies when SubFlow is Passive — parent returns its own Busy status.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenSubFlowPassiveButCorrelationStillActive_ReturnsBusy()
    {
        // Arrange
        var (instance, workflow) = CreateParentWithActiveSubFlow();
        SetupCommonMocks(instance, workflow);
        SetupSubFlowGateway(InstanceStatus.Passive, "suspended");

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Status.ShouldBe(InstanceStatus.Busy);
        result.Result.Value!.Transitions.ShouldBeEmpty();
    }

    /// <summary>
    /// When SubFlow is still Active, the existing behavior is preserved:
    /// SubFlow's Active status and state are returned to the client.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenSubFlowIsStillActive_ReturnsSubFlowActiveStatus()
    {
        // Arrange
        var (instance, workflow) = CreateParentWithActiveSubFlow();
        SetupCommonMocks(instance, workflow);
        SetupSubFlowGateway(InstanceStatus.Active, "sub-review");

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Status.ShouldBe(InstanceStatus.Active);
        result.Result.Value!.State.ShouldBe("sub-review");
    }

    /// <summary>
    /// When there is no active SubFlow correlation, the parent's own status is used normally.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenNoActiveSubFlow_ReturnsParentActiveStatus()
    {
        // Arrange — instance has no SubFlow correlations
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Status.ShouldBe(InstanceStatus.Active);
        // Gateway should NOT have been called since there's no active subflow
        await _instanceQueryGateway.DidNotReceive()
            .GetFunctionWithStateAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (Instance instance, Definitions.Workflow workflow) CreateParentWithActiveSubFlow()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        // AddCorrelation with SubFlowType "S" sets instance.Status = Busy
        var correlation = InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: instanceId,
            parentState: TestState,
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: "S",
            subFlowDomain: "sub-domain",
            subFlowName: "sub-flow",
            subFlowVersion: "1.0.0"
        );
        instance.AddCorrelation(correlation);

        instance.Status.ShouldBe(InstanceStatus.Busy);

        var workflow = BuildWorkflow(state);
        return (instance, workflow);
    }

    private static Definitions.Workflow BuildWorkflow(State state)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));
        workflow.SetType("F");
        workflow.AddState(state);
        return workflow;
    }

    private void SetupCommonMocks(Instance instance, Definitions.Workflow workflow)
    {
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);

        _componentCacheStore
            .GetFlowAsync(TestDomain, TestWorkflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));

        _urlTemplateBuilder.BuildDataUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://data-url");
        _urlTemplateBuilder.BuildViewUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://view-url");

        _representationEtagService.Generate(Arg.Any<object>()).Returns((string?)null);
    }

    private void SetupSubFlowGateway(InstanceStatus subFlowStatus, string subFlowState)
    {
        var subFlowOutput = new GetInstanceStateOutput
        {
            Status = subFlowStatus,
            State = subFlowState,
            Transitions = [],
            ActiveCorrelations = []
        };

        _instanceQueryGateway
            .GetFunctionWithStateAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceStateOutput>.Success(subFlowOutput));
    }

    private static GetInstanceStateInput CreateInput(string instanceId) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instanceId,
        Headers = new Dictionary<string, string?>(),
        QueryParams = new Dictionary<string, string?>()
    };
}
