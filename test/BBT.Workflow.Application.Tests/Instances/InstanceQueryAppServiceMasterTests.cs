using System;
using System.Collections.Generic;
using System.Text.Json;
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
/// Unit tests for InstanceQueryAppService.GetMasterAsync — resolution of the flow-level master
/// schema an instance is bound to, including the active-subflow forwarding path.
/// </summary>
public class InstanceQueryAppServiceMasterTests : IDisposable
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly IComponentCacheStore _componentCacheStore;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IInstanceQueryGateway _instanceQueryGateway;
    private readonly Caching.IInstanceSchemaFunctionCache _instanceSchemaFunctionCache;
    private readonly ITransitionAuthorizationManager _transitionAuthorizationManager;
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string TestState = "review";
    private const string MasterSchemaKey = "account-master";

    public InstanceQueryAppServiceMasterTests()
    {
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _instanceQueryGateway = Substitute.For<IInstanceQueryGateway>();
        // Enabled defaults to false so existing tests exercise the full build path.
        _instanceSchemaFunctionCache = Substitute.For<Caching.IInstanceSchemaFunctionCache>();
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
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            instanceQueryGateway: _instanceQueryGateway,
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
            instanceSchemaFunctionCache: _instanceSchemaFunctionCache,
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task GetMasterAsync_WhenNoSubFlow_ReturnsFlowLevelMasterSchema()
    {
        // Arrange
        var instance = CreateInstance();
        var workflow = BuildWorkflow(withMasterSchema: true);
        SetupCommonMocks(instance, workflow);
        SetupMasterSchema();

        // Act
        var result = await _service.GetMasterAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Key.ShouldBe(MasterSchemaKey);
        result.Result.Value.Type.ShouldBe("JSON");

        // No forwarding when there is no active subflow.
        await _instanceQueryGateway.DidNotReceive()
            .GetFunctionWithMasterAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMasterAsync_WhenActiveSubFlow_ForwardsToSubFlowMaster()
    {
        // Arrange — parent instance with an active subflow correlation
        var instance = CreateInstanceWithActiveSubFlow();
        var workflow = BuildWorkflow(withMasterSchema: true);
        SetupCommonMocks(instance, workflow);
        _instanceQueryGateway
            .GetFunctionWithMasterAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<GetSchemaOutput>.Ok(new GetSchemaOutput
            {
                Key = "subflow-master",
                Type = "JSON",
                Schema = JsonDocument.Parse("{}").RootElement
            }));

        // Act
        var result = await _service.GetMasterAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert — subflow's master schema is returned, parent's own schema is NOT resolved
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Key.ShouldBe("subflow-master");
        await _componentCacheStore.DidNotReceive()
            .GetSchemaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMasterAsync_WhenQueryRolesDeny_Returns403()
    {
        // Arrange
        var instance = CreateInstance();
        var workflow = BuildWorkflow(withMasterSchema: true);
        SetupCommonMocks(instance, workflow);
        SetupMasterSchema();
        _transitionAuthorizationManager
            .IsQueryAllowedAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<Instance>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _service.GetMasterAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeFalse();
        result.Result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
    }

    [Fact]
    public async Task GetMasterAsync_WhenWorkflowHasNoMasterSchema_ReturnsNotFound()
    {
        // Arrange — workflow without a flow-level master schema reference
        var instance = CreateInstance();
        var workflow = BuildWorkflow(withMasterSchema: false);
        SetupCommonMocks(instance, workflow);

        // Act
        var result = await _service.GetMasterAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        // Assert
        result.Result.IsSuccess.ShouldBeFalse();
        result.Result.Error.Code.ShouldBe("notfound");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // ── Master function cache ────────────────────────────────────────────────

    private const string TestCacheKey = "master-fn:test-domain:test-flow:key-under-test";

    private void EnableCache()
    {
        _instanceSchemaFunctionCache.Enabled.Returns(true);
        _instanceSchemaFunctionCache.BuildKey(Arg.Any<GetMasterInput>()).Returns(TestCacheKey);
        _instanceSchemaFunctionCache
            .ComputeEtag(Arg.Any<GetMasterInput>(), Arg.Any<InstanceDataFingerprint>())
            .Returns("etag-current");
    }

    private void SetupFingerprint(Guid instanceId, bool hasActiveSubFlow = false) =>
        _instanceRepository
            .GetDataFingerprintAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new InstanceDataFingerprint(instanceId, "test-key",
                "01JD2G4YV0EXAMPLEULID0000A", "1.0.0", "review", hasActiveSubFlow));

    [Fact]
    public async Task GetMasterAsync_WhenIfNoneMatchMatchesFingerprintEtag_Returns304WithoutBuild()
    {
        var instanceId = Guid.NewGuid();
        EnableCache();
        SetupFingerprint(instanceId);

        var input = CreateInput(instanceId.ToString());
        input.IfNoneMatch = "\"etag-current\"";

        var result = await _service.GetMasterAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeTrue();
        await _instanceSchemaFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMasterAsync_WhenCachedEtagMatchesCurrent_ServesFromCacheWithoutAggregateLoad()
    {
        var instanceId = Guid.NewGuid();
        EnableCache();
        SetupFingerprint(instanceId);
        _instanceSchemaFunctionCache.GetAsync(TestCacheKey, Arg.Any<CancellationToken>())
            .Returns(new Caching.SchemaFunctionCacheEntry
            {
                Etag = "etag-current",
                Output = new GetSchemaOutput { Key = MasterSchemaKey, Type = "JSON" }
            });

        var result = await _service.GetMasterAsync(CreateInput(instanceId.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Key.ShouldBe(MasterSchemaKey);
        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMasterAsync_WhenCacheMiss_BuildsAndWarmsCache()
    {
        var instance = CreateInstance();
        var workflow = BuildWorkflow(withMasterSchema: true);
        SetupCommonMocks(instance, workflow);
        SetupMasterSchema();
        EnableCache();
        SetupFingerprint(instance.Id);

        var result = await _service.GetMasterAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
        await _instanceSchemaFunctionCache.Received(1).SetAsync(
            TestCacheKey,
            Arg.Is<Caching.SchemaFunctionCacheEntry>(e => e.Etag == "etag-current"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMasterAsync_WhenActiveSubFlow_BypassesCacheAndClearsForwardedEtag()
    {
        var instance = CreateInstanceWithActiveSubFlow();
        var workflow = BuildWorkflow(withMasterSchema: true);
        SetupCommonMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id, hasActiveSubFlow: true);
        _instanceQueryGateway
            .GetFunctionWithMasterAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<GetSchemaOutput>.Ok(new GetSchemaOutput
            {
                Key = "sub-master",
                Type = "JSON",
                ETag = "sub-etag-should-not-leak"
            }));

        var result = await _service.GetMasterAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Key.ShouldBe("sub-master");
        result.Result.Value!.ETag.ShouldBeNull();
        await _instanceSchemaFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceSchemaFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.SchemaFunctionCacheEntry>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMasterAsync_WhenNoMasterSchema_DoesNotCacheFailure()
    {
        var instance = CreateInstance();
        var workflow = BuildWorkflow(withMasterSchema: false);
        SetupCommonMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id);

        var result = await _service.GetMasterAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeFalse();
        await _instanceSchemaFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.SchemaFunctionCacheEntry>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static Instance CreateInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);
        return instance;
    }

    private static Instance CreateInstanceWithActiveSubFlow()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        var correlation = InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: instanceId,
            parentState: TestState,
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: "S",
            subFlowDomain: "sub-domain",
            subFlowName: "sub-flow",
            subFlowVersion: "1.0.0");
        instance.AddCorrelation(correlation);
        return instance;
    }

    private static Definitions.Workflow BuildWorkflow(bool withMasterSchema)
    {
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));
        workflow.SetType("F");
        workflow.SetStartTransition(Transition.Create("start", null, state.Key, TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        workflow.AddState(state);
        if (withMasterSchema)
            workflow.SetSchema(new Reference(MasterSchemaKey, TestDomain, "sys-schemas", TestVersion));
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
        _transitionAuthorizationManager
            .IsQueryAllowedAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<Instance>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private void SetupMasterSchema()
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            """{ "type": "JSON", "schema": { "type": "object", "properties": {} } }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference(MasterSchemaKey, TestDomain, "sys-schemas", TestVersion));
        _componentCacheStore
            .GetSchemaAsync(TestDomain, MasterSchemaKey, TestVersion, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
    }

    private static GetMasterInput CreateInput(string instanceId) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instanceId,
        Headers = new Dictionary<string, string?>(),
        QueryParameters = new Dictionary<string, string?>()
    };
}
