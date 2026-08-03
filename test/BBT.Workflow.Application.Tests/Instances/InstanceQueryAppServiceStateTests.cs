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
    private readonly IInstanceCorrelationRepository _instanceCorrelationRepository;
    private readonly Caching.IStateFunctionCache _stateFunctionCache;
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
        _instanceCorrelationRepository = Substitute.For<IInstanceCorrelationRepository>();
        // Enabled defaults to false so existing tests exercise the full build path;
        // cache-specific tests opt in explicitly.
        _stateFunctionCache = Substitute.For<Caching.IStateFunctionCache>();

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
            instanceCorrelationRepository: _instanceCorrelationRepository,
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
            stateFunctionCache: _stateFunctionCache,
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The full <c>correlations</c> list carries completed correlations with their terminal details,
    /// while <c>activeCorrelations</c> keeps its active-only semantics — the compatibility contract for
    /// clients already reading the active list.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_ReturnsCompletedCorrelations_WithoutWideningActiveCorrelations()
    {
        // Arrange — one still-active correlation (from the fixture) plus one that already faulted
        var (instance, workflow) = CreateParentWithActiveSubFlow();
        SetupCommonMocks(instance, workflow);
        SetupSubFlowGateway(InstanceStatus.Active, "sub-review");

        var activeCorrelation = instance.ChildCorrelations.Single();
        var completedAt = new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        // Pinned older than the fixture's active correlation so the expected CreatedAt order is explicit.
        var completedCorrelation = CreateCorrelation(
            instance.Id, "earlier-sub-flow", SubItemTerminalOutcome.Faulted, completedAt, "child-error",
            createdAt: activeCorrelation.CreatedAt.AddMinutes(-5));
        SetupFullCorrelations(instance.Id, [completedCorrelation, activeCorrelation]);

        // Act
        var result = await _service.GetInstanceStateAsync(
            CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        var output = result.Result.Value!;

        output.ActiveCorrelations.Select(c => c.CorrelationId)
            .ShouldBe([activeCorrelation.Id]);

        output.Correlations.Select(c => c.CorrelationId)
            .ShouldBe([completedCorrelation.Id, activeCorrelation.Id]);

        var completedEntry = output.Correlations.Single(c => c.CorrelationId == completedCorrelation.Id);
        completedEntry.IsCompleted.ShouldBeTrue();
        completedEntry.CompletedAt.ShouldBe(completedAt);
        completedEntry.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
        completedEntry.CurrentState.ShouldBe("child-error");
        completedEntry.StateChangedAt.ShouldNotBeNull();
        completedEntry.SubFlowName.ShouldBe("earlier-sub-flow");
        completedEntry.Href.ShouldBe("https://data-url");

        var activeEntry = output.Correlations.Single(c => c.CorrelationId == activeCorrelation.Id);
        activeEntry.IsCompleted.ShouldBeFalse();
        activeEntry.CompletedAt.ShouldBeNull();
        activeEntry.TerminalOutcome.ShouldBeNull();
    }

    /// <summary>
    /// The full list is ordered by creation time ascending regardless of the order the read returns,
    /// so a client can replay sub items in the order the parent started them.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_OrdersCorrelationsByCreatedAtAscending()
    {
        // Arrange — supplied newest-first to prove the service re-orders
        var (instance, workflow) = CreateParentWithActiveSubFlow();
        SetupCommonMocks(instance, workflow);
        SetupSubFlowGateway(InstanceStatus.Active, "sub-review");

        var newest = CreateCorrelation(instance.Id, "third", SubItemTerminalOutcome.Completed,
            createdAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        var middle = CreateCorrelation(instance.Id, "second", SubItemTerminalOutcome.Completed,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var oldest = CreateCorrelation(instance.Id, "first", SubItemTerminalOutcome.Completed,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        SetupFullCorrelations(instance.Id, [newest, middle, oldest]);

        // Act
        var result = await _service.GetInstanceStateAsync(
            CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Correlations.Select(c => c.SubFlowName)
            .ShouldBe(["first", "second", "third"]);
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
    /// When the active subflow signals long-poll termination, the parent bubbles the interaction up
    /// and rewrites the ack href to the parent's own acknowledge endpoint (the instance the client polls).
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenSubFlowAwaitingLongPollAck_BubblesInteractionWithParentAckHref()
    {
        // Arrange — subflow is Active and signals termination with its OWN (child) ack href
        var (instance, workflow) = CreateParentWithActiveSubFlow();
        SetupCommonMocks(instance, workflow);
        SetupSubFlowGateway(InstanceStatus.Active, "sub-review", new InstanceInteractionOutput
        {
            TerminateLongPoll = true,
            FallbackTimeoutSeconds = 90,
            Ack = new BBT.Workflow.Shared.AckHref { Href = "/child/longpoll/ack" }
        });
        _urlTemplateBuilder
            .BuildLongPollAckUrl(TestDomain, TestWorkflow, instance.Id.ToString(), Arg.Any<string?>())
            .Returns("/parent/longpoll/ack");

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert — bubbled up, ack href rewritten to the parent's endpoint, fallback carried through
        result.Result.IsSuccess.ShouldBeTrue();
        var output = result.Result.Value!;
        output.Interaction.ShouldNotBeNull();
        output.Interaction!.TerminateLongPoll.ShouldBeTrue();
        output.Interaction.FallbackTimeoutSeconds.ShouldBe(90);
        output.Interaction.Ack.ShouldNotBeNull();
        output.Interaction.Ack!.Href.ShouldBe("/parent/longpoll/ack");
    }

    /// <summary>
    /// When the active subflow does not signal termination, the parent emits no interaction.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenSubFlowNotAwaiting_NoInteraction()
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
        result.Result.Value!.Interaction.ShouldBeNull();
    }

    /// <summary>
    /// When the current state declares interaction.longPoll with terminate=true, the interaction block is
    /// emitted with the terminate flag, the configured fallback window, and the acknowledge href.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenStateDeclaresLongPollTerminate_EmitsInteractionWithAck()
    {
        // Arrange
        var (instance, workflow) = CreateInstanceWithLongPollState(terminate: true, fallbackSeconds: 45);
        SetupCommonMocks(instance, workflow);
        _urlTemplateBuilder
            .BuildLongPollAckUrl(TestDomain, TestWorkflow, instance.Id.ToString(), Arg.Any<string?>())
            .Returns("/longpoll/ack");

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        var interaction = result.Result.Value!.Interaction;
        interaction.ShouldNotBeNull();
        interaction!.TerminateLongPoll.ShouldBeTrue();
        interaction.FallbackTimeoutSeconds.ShouldBe(45);
        interaction.Ack.ShouldNotBeNull();
        interaction.Ack!.Href.ShouldBe("/longpoll/ack");
    }

    /// <summary>
    /// When the current state declares interaction.longPoll with terminate=false, the interaction block is
    /// still emitted (terminate flag false, fallback window present) but without an acknowledge href.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenStateDeclaresLongPollWithoutTerminate_EmitsInteractionWithoutAck()
    {
        // Arrange
        var (instance, workflow) = CreateInstanceWithLongPollState(terminate: false, fallbackSeconds: 120);
        SetupCommonMocks(instance, workflow);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        var interaction = result.Result.Value!.Interaction;
        interaction.ShouldNotBeNull();
        interaction!.TerminateLongPoll.ShouldBeFalse();
        interaction.FallbackTimeoutSeconds.ShouldBe(120);
        interaction.Ack.ShouldBeNull();
    }

    /// <summary>
    /// When the current state does not declare interaction.longPoll, no interaction block is emitted.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenStateHasNoLongPollDeclaration_NoInteraction()
    {
        // Arrange
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
        result.Result.Value!.Interaction.ShouldBeNull();
    }

    /// <summary>
    /// When the state declares interaction.longPoll.roles and the caller's role is not granted,
    /// no interaction block is emitted (role filtering preserved).
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenLongPollRolesDenyCaller_NoInteraction()
    {
        // Arrange — role grants present; IsAnyRoleAllowedForGrantsAsync defaults to false (caller not allowed)
        var (instance, workflow) = CreateInstanceWithLongPollState(terminate: true, fallbackSeconds: 30, withRoles: true);
        SetupCommonMocks(instance, workflow);

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Interaction.ShouldBeNull();
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
    public async Task GetInstanceStateAsync_WhenWizardStateTransitionHasView_ReturnsTransitionViewAndNoStateView()
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

        // Assert: a wizard state is treated like any other state. The state has no own view,
        // and the transition exposes its own view. No wizard-specific view borrowing/hiding.
        result.Result.IsSuccess.ShouldBeTrue();
        var output = result.Result.Value!;
        output.View.HasView.ShouldBeFalse();
        output.Transitions.Single().View!.HasView.ShouldBeTrue();
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenWizardStateHasOwnView_ReturnsStateView()
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
    public async Task GetViewAsync_WhenWizardStateWithTransitionKey_ReturnsTransitionView()
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

        // Act: the transition's view is requested explicitly — a wizard state gets no special handling.
        var result = await _service.GetViewAsync(new GetViewInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString(),
            Headers = new Dictionary<string, string?>(),
            QueryParameters = new Dictionary<string, string?>()
        }, transitionKey: "continue", CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Key.ShouldBe("transition-view");
    }

    [Fact]
    public async Task GetViewAsync_WhenWizardStateWithoutTransitionKey_ReturnsStateView()
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

    [Fact]
    public async Task GetInstanceStateAsync_WhenAliasHasLabels_ReturnsLocalizedLabelForAcceptLanguage()
    {
        // Arrange
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        state.AddAlias(AliasFromJson("""
            {
                "name": "Değerlendirme Aşamasında",
                "roles": [],
                "labels": [
                    { "label": "Operasyon İncelemesinde", "language": "tr" },
                    { "label": "Under Operational Review", "language": "en" }
                ]
            }
            """));
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        _transitionAuthorizationManager
            .IsRoleAllowedForGrantsAsync(Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var input = CreateInput(instance.Id.ToString());
        input.Headers["accept-language"] = "tr-TR,tr;q=0.9,en-US;q=0.8";

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert — Turkish label returned for the Turkish Accept-Language
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.State.ShouldBe("Operasyon İncelemesinde");
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenAliasLabelsHaveNoLanguageMatch_FallsBackToEnglish()
    {
        // Arrange — labels in de + en, request fr → English fallback
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        state.AddAlias(AliasFromJson("""
            {
                "name": "Fallback Name",
                "roles": [],
                "labels": [
                    { "label": "In Bearbeitung", "language": "de" },
                    { "label": "Under Review", "language": "en" }
                ]
            }
            """));
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        _transitionAuthorizationManager
            .IsRoleAllowedForGrantsAsync(Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var input = CreateInput(instance.Id.ToString());
        input.Headers["accept-language"] = "fr-FR";

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.State.ShouldBe("Under Review");
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenMatchingAliasHasNoLabels_ReturnsAliasName()
    {
        // Arrange — alias has labels omitted; even with an Accept-Language header, name is used
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
        input.Headers["accept-language"] = "tr-TR";

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.State.ShouldBe("Değerlendirme Aşamasında");
    }

    [Fact]
    public async Task GetInstanceStateAsync_WhenQueryRolesDeny_Returns403()
    {
        // Arrange
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        DenyQueryRoles();

        var input = CreateInput(instance.Id.ToString());

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.IsNotModified.ShouldBeFalse();
        result.Result.IsSuccess.ShouldBeFalse();
        result.Result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
    }

    [Fact]
    public async Task GetViewAsync_WhenQueryRolesDeny_Returns403()
    {
        // Arrange
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        DenyQueryRoles();

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
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
    }

    [Fact]
    public async Task GetSchemaAsync_WhenQueryRolesDeny_Returns403()
    {
        // Arrange
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        SetupCommonMocks(instance, workflow);
        DenyQueryRoles();

        // Act — denied before the transition-key requirement is evaluated
        var result = await _service.GetSchemaAsync(new GetSchemaInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString()
        }, transitionKey: "approve", CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeFalse();
        result.Result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
    }

    [Fact]
    public async Task GetInstanceStateAsync_AlwaysIncludesMasterHref()
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

        // Assert — the state response exposes the master function endpoint as an href
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Master.ShouldNotBeNull();
        result.Result.Value!.Master.Href.ShouldBe("https://master-url");
    }

    // ── State Function Cache ─────────────────────────────────────────────────

    /// <summary>
    /// When the cache is disabled (default), neither the fingerprint query nor the cache runs;
    /// the full build path answers.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenCacheDisabled_DoesNotTouchCacheOrFingerprint()
    {
        // Arrange — _stateFunctionCache.Enabled defaults to false
        var (instance, workflow) = CreateSimpleActiveInstance();
        SetupCommonMocks(instance, workflow);

        // Act
        var result = await _service.GetInstanceStateAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        _stateFunctionCache.DidNotReceive().BuildKey(Arg.Any<GetInstanceStateInput>());
        await _stateFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _stateFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.StateFunctionCacheEntry>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .GetStateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A matching If-None-Match is answered with 304 straight from the fingerprint projection:
    /// no cache access, no aggregate load, no response build — even with an empty cache.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenIfNoneMatchMatchesFingerprintEtag_Returns304WithoutCacheOrBuild()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        EnableCache();
        SetupFingerprint(instanceId);

        var input = CreateInput(instanceId.ToString());
        input.IfNoneMatch = "\"etag-current\"";

        // Act
        var result = await _service.GetInstanceStateAsync(input, CancellationToken.None);

        // Assert
        result.IsNotModified.ShouldBeTrue();
        await _stateFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Without a current If-None-Match, a cache entry carrying the current fingerprint ETag
    /// serves the cached response without loading the aggregate.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenCachedEtagMatchesCurrent_ServesFromCacheWithoutAggregateLoad()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        EnableCache();
        SetupFingerprint(instanceId);
        SetupCachedEntry(out var entry);

        // Act
        var result = await _service.GetInstanceStateAsync(CreateInput(instanceId.ToString()), CancellationToken.None);

        // Assert — cached output served, aggregate never loaded
        result.IsNotModified.ShouldBeFalse();
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.State.ShouldBe(entry.Output.State);
        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
        result.Result.Value!.EntityEtag.ShouldBe("\"entity-1\"");
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A cache miss runs the full build path and warms the cache under the same fingerprint ETag
    /// the fast path computes.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenCacheMiss_BuildsAndWarmsCacheWithFingerprintEtag()
    {
        // Arrange
        var (instance, workflow) = CreateSimpleActiveInstance();
        SetupCommonMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id);

        // Act
        var result = await _service.GetInstanceStateAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert — response built normally and stored under the deterministic ETag
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
        await _stateFunctionCache.Received(1).SetAsync(
            TestCacheKey,
            Arg.Is<Caching.StateFunctionCacheEntry>(e => e.Etag == "etag-current"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A stale cache entry (its ETag no longer matches the fingerprint ETag — state, status,
    /// version, or resolved row changed) forces a rebuild and refreshes the cache.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenCachedEtagIsStale_RebuildsAndRefreshesCache()
    {
        // Arrange — entry was built under an older fingerprint
        var (instance, workflow) = CreateSimpleActiveInstance();
        SetupCommonMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id);
        SetupCachedEntry(out _, etag: "etag-old");

        // Act
        var result = await _service.GetInstanceStateAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert — full rebuild + cache refresh under the current ETag
        result.Result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
        await _stateFunctionCache.Received(1).SetAsync(
            TestCacheKey,
            Arg.Is<Caching.StateFunctionCacheEntry>(e => e.Etag == "etag-current"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When the fingerprint reports an active SubFlow, both the 304 fast path and the cache are
    /// bypassed (live evaluation required) and nothing is written back.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenFingerprintHasActiveSubFlow_BypassesCache()
    {
        // Arrange
        var (instance, workflow) = CreateParentWithActiveSubFlow();
        SetupCommonMocks(instance, workflow);
        SetupSubFlowGateway(InstanceStatus.Active, "sub-review");
        EnableCache();
        _instanceRepository
            .GetStateFingerprintAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(new InstanceStateFingerprint(instance.Id, "test-key", TestState, InstanceStatus.Busy,
                TestVersion, HasActiveSubFlow: true,
                CorrelationCount: 1, CompletedCorrelationCount: 0,
                LastCorrelationCompletedAt: null, LastSubFlowStateChangedAt: null));

        // Act
        var result = await _service.GetInstanceStateAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert — served live from the subflow gateway with the subflow ETag variant, never cached
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.State.ShouldBe("sub-review");
        result.Result.Value!.ETag.ShouldBe("\"etag-subflow\"");
        await _stateFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _stateFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.StateFunctionCacheEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When the fingerprint query finds no instance, the full path runs and produces its own outcome.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_WhenFingerprintNotFound_FallsThroughToFullPath()
    {
        // Arrange
        var (instance, workflow) = CreateSimpleActiveInstance();
        SetupCommonMocks(instance, workflow);
        EnableCache();
        _instanceRepository
            .GetStateFingerprintAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns((InstanceStateFingerprint?)null);

        // Act
        var result = await _service.GetInstanceStateAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert — the full path answered (here: success, since the aggregate is mocked)
        result.Result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
    }

    // ── Cache helpers ────────────────────────────────────────────────────────

    private const string TestCacheKey = "state-fn:test-domain:test-flow:key-under-test";

    private void EnableCache()
    {
        _stateFunctionCache.Enabled.Returns(true);
        _stateFunctionCache.BuildKey(Arg.Any<GetInstanceStateInput>()).Returns(TestCacheKey);
        _stateFunctionCache
            .ComputeEtag(Arg.Any<GetInstanceStateInput>(), Arg.Any<InstanceStateFingerprint>())
            .Returns("etag-current");
        _stateFunctionCache
            .ComputeEtag(Arg.Any<GetInstanceStateInput>(), Arg.Any<InstanceStateFingerprint>(),
                Arg.Any<GetInstanceStateOutput>())
            .Returns("etag-subflow");
    }

    private void SetupFingerprint(Guid instanceId) =>
        _instanceRepository
            .GetStateFingerprintAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new InstanceStateFingerprint(instanceId, "test-key", TestState, InstanceStatus.Active,
                TestVersion, HasActiveSubFlow: false,
                CorrelationCount: 0, CompletedCorrelationCount: 0,
                LastCorrelationCompletedAt: null, LastSubFlowStateChangedAt: null));

    private void SetupCachedEntry(out Caching.StateFunctionCacheEntry entry, string etag = "etag-current")
    {
        entry = new Caching.StateFunctionCacheEntry
        {
            Etag = etag,
            EntityEtag = "entity-1",
            Output = new GetInstanceStateOutput
            {
                State = TestState,
                Status = InstanceStatus.Active,
                Transitions = [],
                ActiveCorrelations = []
            }
        };
        _stateFunctionCache.GetAsync(TestCacheKey, Arg.Any<CancellationToken>()).Returns(entry);
    }

    private (Instance instance, Definitions.Workflow workflow) CreateSimpleActiveInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);
        var workflow = BuildWorkflow(state);
        return (instance, workflow);
    }

    private void DenyQueryRoles() =>
        _transitionAuthorizationManager
            .IsQueryAllowedAsync(
                Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(),
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

    private static StateAlias AliasFromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<StateAlias>(json, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        })!;

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
        _urlTemplateBuilder.BuildMasterUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://master-url");

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

        // Full correlation set for the `correlations` response list and the fingerprint. Defaults to
        // the aggregate's own (active) correlations so tests that do not care about completed rows
        // see a consistent picture; SetupFullCorrelations overrides it.
        SetupFullCorrelations(instance.Id, [.. instance.ChildCorrelations]);

        // Default: queryRoles visibility allowed (specific tests override to false to assert 403).
        _transitionAuthorizationManager
            .IsQueryAllowedAsync(
                Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(),
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
    }

    /// <summary>
    /// Stubs the dedicated full-correlation read (active + completed) the state path performs, since the
    /// aggregate's own collection is loaded active-only in production.
    /// </summary>
    private void SetupFullCorrelations(Guid instanceId, List<InstanceCorrelation> correlations) =>
        _instanceCorrelationRepository
            .GetByParentAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(correlations);

    /// <summary>
    /// Builds a correlation for <paramref name="parentInstanceId"/>, optionally already terminated.
    /// </summary>
    private static InstanceCorrelation CreateCorrelation(
        Guid parentInstanceId,
        string subFlowName,
        SubItemTerminalOutcome? outcome = null,
        DateTime? completedAt = null,
        string? subFlowCurrentState = null,
        DateTime? createdAt = null)
    {
        var correlation = InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: parentInstanceId,
            parentState: TestState,
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: "S",
            subFlowDomain: "sub-domain",
            subFlowName: subFlowName,
            subFlowVersion: "1.0.0");

        if (subFlowCurrentState is not null)
            correlation.UpdateSubFlowState(subFlowCurrentState, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        if (outcome is not null)
            correlation.ApplyTerminalOutcome(outcome.Value,
                completedAt ?? new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc));

        if (createdAt is not null)
            PinCreatedAt(correlation, createdAt.Value);

        return correlation;
    }

    /// <summary>
    /// Pins a correlation's <c>CreatedAt</c>. The audit base exposes only a protected setter (production
    /// stamps it on save), so ordering assertions have to reach it reflectively.
    /// </summary>
    private static void PinCreatedAt(InstanceCorrelation correlation, DateTime createdAt) =>
        typeof(InstanceCorrelation)
            .GetProperty(nameof(InstanceCorrelation.CreatedAt))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(correlation, [createdAt]);

    private void SetupSubFlowGateway(InstanceStatus subFlowStatus, string subFlowState,
        InstanceInteractionOutput? interaction = null)
    {
        var subFlowOutput = new GetInstanceStateOutput
        {
            Status = subFlowStatus,
            State = subFlowState,
            Transitions = [],
            ActiveCorrelations = [],
            Interaction = interaction
        };

        _instanceQueryGateway
            .GetFunctionWithStateAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceStateOutput>.Success(subFlowOutput));
    }

    /// <summary>
    /// Builds an instance whose current state ("review") declares interaction.longPoll, deserialized from
    /// JSON since the interaction value objects expose only private (JSON) constructors.
    /// </summary>
    private static (Instance instance, Definitions.Workflow workflow) CreateInstanceWithLongPollState(
        bool terminate, int fallbackSeconds, bool withRoles = false)
    {
        var roles = withRoles
            ? """, "roles": [ { "role": "FullAuthorized", "grant": "allow" } ]"""
            : "";
        var terminateLiteral = terminate ? "true" : "false";
        var json = $$"""
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           { "key": "review", "stateType": "Intermediate", "transitions": [],
                             "interaction": { "longPoll": { "terminate": {{terminateLiteral}}, "fallbackTimeoutSeconds": {{fallbackSeconds}}{{roles}} } } }
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "review", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));

        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var reviewState = workflow.States.First(s => s.Key == "review");
        instance.ChangeState(reviewState);
        return (instance, workflow);
    }

    /// <summary>
    /// The workflow-level updateData and exit transitions are surfaced to clients as regular
    /// available transitions, each carrying its own kind so the Client Workflow Manager SDK can
    /// tell them apart from state and shared transitions.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_ExposesUpdateDataAndExit_WithTheirOwnKinds()
    {
        // Arrange
        var (instance, workflow) = CreateActiveInstanceWithWellKnownTransitions();
        SetupCommonMocks(instance, workflow);

        // Act
        var result = await _service.GetInstanceStateAsync(
            CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        var transitions = result.Result.Value!.Transitions!;

        transitions.Select(t => t.Name).ShouldBe(
            ["submit", "cancel", "update-data", "exit"], ignoreOrder: true);
        transitions.Single(t => t.Name == "submit").Kind.ShouldBe("stateTransition");
        transitions.Single(t => t.Name == "cancel").Kind.ShouldBe("cancel");
        transitions.Single(t => t.Name == "exit").Kind.ShouldBe("exit");
        // Deliberately "updateData" (the workflow-definition field name), not the
        // "update-parent-data" well-known request alias.
        transitions.Single(t => t.Name == "update-data").Kind.ShouldBe("updateData");
    }

    /// <summary>
    /// Role grants on updateData/exit are now live: a caller the authorization manager rejects
    /// must not see them in availableTransitions.
    /// </summary>
    [Fact]
    public async Task GetInstanceStateAsync_OmitsUpdateDataAndExit_WhenCallerIsNotAuthorized()
    {
        // Arrange
        var (instance, workflow) = CreateActiveInstanceWithWellKnownTransitions();
        SetupCommonMocks(instance, workflow);

        _transitionAuthorizationManager
            .FilterAuthorizedTransitionKeysAsync(
                Arg.Any<Definitions.Workflow>(),
                Arg.Any<State>(),
                Arg.Any<Instance?>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<IReadOnlyList<string>>(
                [.. callInfo.ArgAt<IReadOnlyList<string>>(3)
                    .Where(k => k is not ("update-data" or "exit"))]));

        // Act
        var result = await _service.GetInstanceStateAsync(
            CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Transitions!.Select(t => t.Name).ShouldBe(["submit", "cancel"], ignoreOrder: true);
    }

    /// <summary>
    /// Builds an Active instance in an intermediate state whose workflow declares one state
    /// transition plus all three well-known workflow-level transitions.
    /// </summary>
    private static (Instance Instance, Definitions.Workflow Workflow) CreateActiveInstanceWithWellKnownTransitions()
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        state.AddTransition(Transition.Create("submit", TestState, "done", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        instance.ChangeState(state);

        var workflow = BuildWorkflow(state);
        workflow.SetCancel(Transition.Create("cancel", null, "cancelled", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        workflow.SetUpdateData(Transition.Create("update-data", null, "$self", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        workflow.SetExit(Transition.Create("exit", null, "exited", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));

        return (instance, workflow);
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
