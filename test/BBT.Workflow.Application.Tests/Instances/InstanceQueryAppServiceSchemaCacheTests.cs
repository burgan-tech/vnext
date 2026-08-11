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
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Extentions;
using BBT.Workflow.Selection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for the schema-function fingerprint-ETag fast path and cache flow in
/// <see cref="InstanceQueryAppService.GetSchemaAsync"/>: 304 from the projection alone,
/// cached 200 without aggregate load, transition-key scoping, subflow bypass, and
/// no caching of failure outcomes.
/// </summary>
public class InstanceQueryAppServiceSchemaCacheTests : IDisposable
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly IComponentCacheStore _componentCacheStore;
    private readonly IInstanceRepository _instanceRepository;
    private readonly ITransitionAuthorizationManager _transitionAuthorizationManager;
    private readonly Caching.IInstanceSchemaFunctionCache _instanceSchemaFunctionCache;
    private readonly ITaskConditionService _conditionService = Substitute.For<ITaskConditionService>();
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string TestState = "review";
    private const string TestTransition = "approve";
    private const string TestCacheKey = "schema-fn:test-domain:test-flow:key-under-test:approve";

    public InstanceQueryAppServiceSchemaCacheTests()
    {
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _transitionAuthorizationManager = Substitute.For<ITransitionAuthorizationManager>();
        _instanceSchemaFunctionCache = Substitute.For<Caching.IInstanceSchemaFunctionCache>();

        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager
            .BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockUoWManager);
        _ambientServiceProvider = services.BuildServiceProvider();

        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        _transitionAuthorizationManager
            .IsQueryAllowedAsync(
                Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(),
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        _service = new InstanceQueryAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: _runtimeInfoProvider,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceTransitionRepository: Substitute.For<IInstanceTransitionRepository>(),
            instanceCorrelationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            transitionSchemaResolver: new TransitionSchemaResolver(
                new RuleBasedSelectionResolver(_conditionService),
                Substitute.For<ILogger<TransitionSchemaResolver>>()),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: Substitute.For<IUrlTemplateBuilder>(),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: _transitionAuthorizationManager,
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            currentUser: Substitute.For<ICurrentUser>(),
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
    public async Task GetSchemaAsync_WhenCacheDisabled_DoesNotTouchCacheOrFingerprint()
    {
        var (instance, workflow) = CreateInstanceWithTransitionSchema();
        SetupFullPathMocks(instance, workflow);

        var result = await _service.GetSchemaAsync(CreateInput(instance.Id.ToString()), TestTransition, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        _instanceSchemaFunctionCache.DidNotReceive().BuildKey(Arg.Any<GetSchemaInput>(), Arg.Any<string>());
        await _instanceRepository.DidNotReceive()
            .GetDataFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSchemaAsync_WhenIfNoneMatchMatchesFingerprintEtag_Returns304WithoutBuild()
    {
        var instanceId = Guid.NewGuid();
        EnableCache();
        SetupFingerprint(instanceId);

        var input = CreateInput(instanceId.ToString());
        input.IfNoneMatch = "\"etag-current\"";

        var result = await _service.GetSchemaAsync(input, TestTransition, CancellationToken.None);

        result.IsNotModified.ShouldBeTrue();
        await _instanceSchemaFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSchemaAsync_WhenCachedEtagMatchesCurrent_ServesFromCacheWithoutAggregateLoad()
    {
        var instanceId = Guid.NewGuid();
        EnableCache();
        SetupFingerprint(instanceId);
        _instanceSchemaFunctionCache.GetAsync(TestCacheKey, Arg.Any<CancellationToken>())
            .Returns(new Caching.SchemaFunctionCacheEntry
            {
                Etag = "etag-current",
                Output = new GetSchemaOutput { Key = "approve-schema", Type = "JSON" }
            });

        var result = await _service.GetSchemaAsync(CreateInput(instanceId.ToString()), TestTransition, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Key.ShouldBe("approve-schema");
        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSchemaAsync_WhenCacheMiss_BuildsAndWarmsCache()
    {
        var (instance, workflow) = CreateInstanceWithTransitionSchema();
        SetupFullPathMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id);

        var result = await _service.GetSchemaAsync(CreateInput(instance.Id.ToString()), TestTransition, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
        await _instanceSchemaFunctionCache.Received(1).SetAsync(
            TestCacheKey,
            Arg.Is<Caching.SchemaFunctionCacheEntry>(e => e.Etag == "etag-current"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSchemaAsync_WhenFingerprintHasActiveSubFlow_BypassesFastPath()
    {
        var (instance, workflow) = CreateInstanceWithTransitionSchema();
        SetupFullPathMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id, hasActiveSubFlow: true);

        var result = await _service.GetSchemaAsync(CreateInput(instance.Id.ToString()), TestTransition, CancellationToken.None);

        // Instance itself has no active subflow in this arrangement, so the full path resolves
        // locally — the point is the fast path never consulted the cache.
        result.Result.IsSuccess.ShouldBeTrue();
        await _instanceSchemaFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSchemaAsync_WhenTransitionKeyMissing_FailsWithoutCaching()
    {
        var (instance, workflow) = CreateInstanceWithTransitionSchema();
        SetupFullPathMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id);

        var result = await _service.GetSchemaAsync(CreateInput(instance.Id.ToString()), transitionKey: null, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeFalse();
        _instanceSchemaFunctionCache.DidNotReceive().BuildKey(Arg.Any<GetSchemaInput>(), Arg.Any<string>());
        await _instanceSchemaFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.SchemaFunctionCacheEntry>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSchemaAsync_WhenTransitionHasNoSchema_DoesNotCacheFailure()
    {
        var (instance, workflow) = CreateInstanceWithTransitionSchema(withSchemaRef: false);
        SetupFullPathMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id);

        var result = await _service.GetSchemaAsync(CreateInput(instance.Id.ToString()), TestTransition, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeFalse();
        await _instanceSchemaFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.SchemaFunctionCacheEntry>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSchemaAsync_WhenTransitionSchemaIsRuleBased_BypassesFastPathAndDoesNotCacheOrEtag()
    {
        var (instance, workflow) = CreateInstanceWithRuleBasedTransitionSchema();
        SetupFullPathMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id);
        // The rule matches, so the mobile entry wins over the trailing rule-less fallback.
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(true));

        var result = await _service.GetSchemaAsync(
            CreateInput(instance.Id.ToString()), TestTransition, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Key.ShouldBe("approve-schema-mobile");

        // A rule reads headers and query parameters, which the caller-scope hash does not cover — so the
        // response must never be cached nor represented by the fingerprint ETag.
        result.Result.Value!.ETag.ShouldBeNull();
        await _instanceSchemaFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceSchemaFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.SchemaFunctionCacheEntry>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSchemaAsync_WhenRuleBasedAndIfNoneMatchSupplied_StillReturnsBodyNever304()
    {
        var (instance, workflow) = CreateInstanceWithRuleBasedTransitionSchema();
        SetupFullPathMocks(instance, workflow);
        EnableCache();
        SetupFingerprint(instance.Id);
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(true));

        var input = CreateInput(instance.Id.ToString());
        input.IfNoneMatch = "\"etag-current\"";

        var result = await _service.GetSchemaAsync(input, TestTransition, CancellationToken.None);

        result.IsNotModified.ShouldBeFalse();
        result.Result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task GetSchemaAsync_WhenRuleDoesNotMatch_FallsBackToTheRulelessEntry()
    {
        var (instance, workflow) = CreateInstanceWithRuleBasedTransitionSchema();
        SetupFullPathMocks(instance, workflow);
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(false));

        var result = await _service.GetSchemaAsync(
            CreateInput(instance.Id.ToString()), TestTransition, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Key.ShouldBe("approve-schema");
    }

    [Fact]
    public async Task GetSchemaAsync_WhenRuleThrows_SkipsEntryRatherThanFailing()
    {
        var (instance, workflow) = CreateInstanceWithRuleBasedTransitionSchema();
        SetupFullPathMocks(instance, workflow);
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Fail(Error.Failure("script", "boom")));

        var result = await _service.GetSchemaAsync(
            CreateInput(instance.Id.ToString()), TestTransition, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Key.ShouldBe("approve-schema");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void EnableCache()
    {
        _instanceSchemaFunctionCache.Enabled.Returns(true);
        _instanceSchemaFunctionCache.BuildKey(Arg.Any<GetSchemaInput>(), Arg.Any<string>()).Returns(TestCacheKey);
        _instanceSchemaFunctionCache
            .ComputeEtag(Arg.Any<GetSchemaInput>(), Arg.Any<InstanceDataFingerprint>(), Arg.Any<string>())
            .Returns("etag-current");
    }

    private void SetupFingerprint(Guid instanceId, bool hasActiveSubFlow = false) =>
        _instanceRepository
            .GetDataFingerprintAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new InstanceDataFingerprint(instanceId, "test-key",
                "01JD2G4YV0EXAMPLEULID0000A", TestVersion, TestState, hasActiveSubFlow));

    private static (Instance instance, Definitions.Workflow workflow) CreateInstanceWithTransitionSchema(
        bool withSchemaRef = true)
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        var transition = Transition.Create(TestTransition, TestState, "approved", TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code);
        if (withSchemaRef)
            transition.SetSchema(new Reference("approve-schema", TestDomain, "sys-schemas", TestVersion));
        state.AddTransition(transition);
        instance.ChangeState(state);

        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));
        workflow.SetType("F");
        workflow.SetStartTransition(Transition.Create("start", null, TestState, TriggerType.Manual,
            VersionStrategy.IncreasePatch.Code));
        workflow.AddState(state);
        return (instance, workflow);
    }

    /// <summary>
    /// A transition whose schema is authored as two entries: a rule-bearing mobile schema followed by a
    /// rule-less fallback — the shape rule-based selection exists for.
    /// </summary>
    private static (Instance instance, Definitions.Workflow workflow) CreateInstanceWithRuleBasedTransitionSchema()
    {
        var (instance, workflow) = CreateInstanceWithTransitionSchema(withSchemaRef: false);
        var transition = workflow.GetState(TestState).Value!.Transitions.First(t => t.Key == TestTransition);

        transition.SetSchema(SchemaSelection.CreateWithSchemas(
            SchemaEntry.CreateWithRule(
                new Reference("approve-schema-mobile", TestDomain, "sys-schemas", TestVersion),
                new ScriptCode("inline", "true")),
            SchemaEntry.CreateDefault(
                new Reference("approve-schema", TestDomain, "sys-schemas", TestVersion))));

        return (instance, workflow);
    }

    private void SetupFullPathMocks(Instance instance, Definitions.Workflow workflow)
    {
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);

        _componentCacheStore
            .GetFlowAsync(TestDomain, TestWorkflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));

        _componentCacheStore
            .GetSchemaAsync(TestDomain, "approve-schema", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(SchemaFromJson()));

        _componentCacheStore
            .GetSchemaAsync(TestDomain, "approve-schema-mobile", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(SchemaFromJson("approve-schema-mobile")));
    }

    private static SchemaDefinition SchemaFromJson(string key = "approve-schema") =>
        System.Text.Json.JsonSerializer.Deserialize<SchemaDefinition>($$"""
            { "key": "{{key}}", "type": "JSON", "schema": { "type": "object" } }
            """, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static GetSchemaInput CreateInput(string instanceId) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instanceId,
        Headers = new Dictionary<string, string?>(),
        QueryParameters = new Dictionary<string, string?>()
    };
}
