using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Aether.MultiSchema;
using BBT.Aether.Users;
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
    private readonly IViewContentResolutionService _viewContentResolutionService;
    private readonly ITransitionAuthorizationManager _transitionAuthorizationManager;
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
        _viewContentResolutionService = Substitute.For<IViewContentResolutionService>();
        _transitionAuthorizationManager = Substitute.For<ITransitionAuthorizationManager>();

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
            viewContentResolutionService: _viewContentResolutionService,
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: _urlTemplateBuilder,
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: _transitionAuthorizationManager,
            representationEtagService: _representationEtagService,
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            currentUser: Substitute.For<ICurrentUser>(),
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

    [Fact]
    public async Task GetInstanceStateAsync_WhenCurrentStateIsWizard_ReturnsStateTypeAsCamelCase()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Wizard, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.StateType.ShouldBe("wizard");
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenTransitionsAreAvailable_ReturnsTransitionKind()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        var stateTransition = Transition.Create("approve", TestState, "approved", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code);
        state.AddTransition(stateTransition);
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        workflow.AddSharedTransition(Transition.Create("add-note", null, "$self", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        workflow.SetCancel(Transition.Create("cancel-request", null, "cancelled", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        SetupCommonMocks(instance, workflow);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        var transitions = result.Result.Value!.Transitions.ToDictionary(t => t.Name);
        transitions["approve"].Kind.ShouldBe("stateTransition");
        transitions["add-note"].Kind.ShouldBe("sharedTransition");
        transitions["cancel-request"].Kind.ShouldBe("cancel");
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenWizardStateTransitionHasView_ReturnsStateHasViewAndHidesTransitionView()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Wizard, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        var transition = Transition.Create("continue", TestState, "next", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code);
        transition.SetView(ViewDefinition.CreateDefault(
            new Reference("transition-view", TestDomain, "sys-views", TestVersion),
            loadData: true));
        state.AddTransition(transition);
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        var output = result.Result.Value!;
        output.View.HasView.ShouldBeTrue();
        output.View.LoadData.ShouldBeTrue();
        output.Transitions.Single().View!.HasView.ShouldBeFalse();
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenWizardStateTransitionHasNoView_FallsBackToStateView()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Wizard, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        state.SetView(ViewDefinition.CreateDefault(
            new Reference("state-view", TestDomain, "sys-views", TestVersion),
            loadData: false));
        state.AddTransition(Transition.Create("continue", TestState, "next", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        var output = result.Result.Value!;
        output.View.HasView.ShouldBeTrue();
        output.View.LoadData.ShouldBeFalse();
        output.Transitions.Single().View!.HasView.ShouldBeFalse();
    }

    [Fact]
    public async Task GetViewAsync_WhenWizardStateTransitionHasView_ReturnsTransitionView()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Wizard, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        var transitionViewRef = new Reference("transition-view", TestDomain, "sys-views", TestVersion);
        var stateViewRef = new Reference("state-view", TestDomain, "sys-views", TestVersion);
        var transition = Transition.Create("continue", TestState, "next", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code);
        transition.SetView(ViewDefinition.CreateDefault(transitionViewRef));
        state.SetView(ViewDefinition.CreateDefault(stateViewRef));
        state.AddTransition(transition);
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        _viewContentResolutionService
            .ResolveViewContentAsync(transitionViewRef, TestDomain, Arg.Any<Dictionary<string, string?>?>(),
                Arg.Any<Dictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<GetViewOutput>.Ok(new GetViewOutput
            {
                Key = "transition-view",
                Type = "Json",
                Display = "Page",
                Label = string.Empty
            }));

        // Act
        var result = await _service.GetViewAsync(new GetViewInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString(),
            Headers = new Dictionary<string, string?>(),
            QueryParameters = new Dictionary<string, string?>()
        }, transitionKey: null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Key.ShouldBe("transition-view");
    }

    [Fact]
    public async Task GetViewAsync_WhenWizardStateTransitionHasNoView_ReturnsStateView()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Wizard, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        var stateViewRef = new Reference("state-view", TestDomain, "sys-views", TestVersion);
        state.SetView(ViewDefinition.CreateDefault(stateViewRef));
        state.AddTransition(Transition.Create("continue", TestState, "next", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        _viewContentResolutionService
            .ResolveViewContentAsync(stateViewRef, TestDomain, Arg.Any<Dictionary<string, string?>?>(),
                Arg.Any<Dictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<GetViewOutput>.Ok(new GetViewOutput
            {
                Key = "state-view",
                Type = "Json",
                Display = "Page",
                Label = string.Empty
            }));

        // Act
        var result = await _service.GetViewAsync(new GetViewInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString(),
            Headers = new Dictionary<string, string?>(),
            QueryParameters = new Dictionary<string, string?>()
        }, transitionKey: null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Key.ShouldBe("state-view");
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

    [Fact]
    public async Task GetInstanceStateAsync_WhenStateHasMatchingAlias_ReturnsAliasNameAsState()
    {
        // Arrange
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        state.AddAlias(StateAlias.Create("Değerlendirme Aşamasında"));
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        _transitionAuthorizationManager
            .IsRoleAllowedForGrantsAsync(Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert — state reflects the resolved alias name, StateType still reflects the real type
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.State.ShouldBe("Değerlendirme Aşamasında");
        result.Result.Value!.StateType.ShouldBe("intermediate");
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenNoAliasMatches_ReturnsRealStateKey()
    {
        // Arrange
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        state.AddAlias(StateAlias.Create("Backoffice Only"));
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        _transitionAuthorizationManager
            .IsRoleAllowedForGrantsAsync(Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert — falls back to the raw state key
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.State.ShouldBe(TestState);
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenNoAliasesDefined_ReturnsRealStateKeyWithoutEvaluation()
    {
        // Arrange
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert — current behavior preserved; role evaluation skipped entirely
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.State.ShouldBe(TestState);
        await _transitionAuthorizationManager.DidNotReceive()
            .IsRoleAllowedForGrantsAsync(Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenMultipleAliases_ReturnsFirstMatchingInDeclarationOrder()
    {
        // Arrange — first alias does not resolve, second does
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        state.AddAlias(StateAlias.Create("Operasyon İncelemesinde"));
        state.AddAlias(StateAlias.Create("Değerlendirme Aşamasında"));
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        _transitionAuthorizationManager
            .IsRoleAllowedForGrantsAsync(Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(false, true);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.State.ShouldBe("Değerlendirme Aşamasında");
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
        workflow.SetStartTransition(Transition.Create("start", null, state.Key, TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
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
        _urlTemplateBuilder.BuildViewUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://transition-view-url");
        _urlTemplateBuilder.BuildTransitionUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://transition-url");
        _urlTemplateBuilder.BuildSchemaUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://schema-url");

        _transitionAuthorizationManager
            .FilterAuthorizedTransitionKeysAsync(
                Arg.Any<Definitions.Workflow>(),
                Arg.Any<State>(),
                Arg.Any<Instance?>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<IReadOnlyList<string>>(3)));

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
