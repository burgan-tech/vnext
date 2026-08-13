using System;
using System.Collections.Generic;
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
/// Unit tests for the data-function fingerprint-ETag fast path and cache flow in
/// <see cref="InstanceQueryAppService.GetInstanceDataAsync"/>: 304 from the projection alone,
/// cached 200 without aggregate load, latest-only restriction (pinned versions bypass),
/// warm-on-build with workflow-author TTL, and stale-entry invalidation.
/// </summary>
public class InstanceQueryAppServiceDataCacheTests : IDisposable
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly IComponentCacheStore _componentCacheStore;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IInstanceExtensionService _instanceExtensionService;
    private readonly IScriptContextFactory _scriptContextFactory;
    private readonly ITransitionAuthorizationManager _transitionAuthorizationManager;
    private readonly ISchemaFieldFilterService _schemaFieldFilterService;
    private readonly Caching.IDataFunctionCache _dataFunctionCache;
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string TestCacheKey = "data-fn:test-domain:test-flow:key-under-test";

    public InstanceQueryAppServiceDataCacheTests()
    {
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _instanceExtensionService = Substitute.For<IInstanceExtensionService>();
        _scriptContextFactory = Substitute.For<IScriptContextFactory>();
        _transitionAuthorizationManager = Substitute.For<ITransitionAuthorizationManager>();
        _schemaFieldFilterService = Substitute.For<ISchemaFieldFilterService>();
        _dataFunctionCache = Substitute.For<Caching.IDataFunctionCache>();

        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager
            .BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockUoWManager);
        services.AddSingleton(Substitute.For<IComponentCacheStore>());
        services.AddSingleton(Substitute.For<BBT.Workflow.DefinitionContext.IWorkflowContext>());
        _ambientServiceProvider = services.BuildServiceProvider();

        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        var scriptContextBuilder = Substitute.For<IScriptContextBuilder>();
        scriptContextBuilder.WithWorkflow(Arg.Any<Definitions.Workflow?>()).Returns(scriptContextBuilder);
        scriptContextBuilder.WithInstance(Arg.Any<Instance>()).Returns(scriptContextBuilder);
        scriptContextBuilder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(scriptContextBuilder);
        scriptContextBuilder.WithTransition(Arg.Any<string>()).Returns(scriptContextBuilder);
        scriptContextBuilder.WithBody(Arg.Any<JsonData>()).Returns(scriptContextBuilder);
        scriptContextBuilder.WithHeaders(Arg.Any<Dictionary<string, string?>?>()).Returns(scriptContextBuilder);
        scriptContextBuilder.WithQueryParameters(Arg.Any<Dictionary<string, string?>?>()).Returns(scriptContextBuilder);
        scriptContextBuilder.BuildAsync(Arg.Any<CancellationToken>())
            .Returns(new ScriptContext(Substitute.For<ILogger<ScriptContext>>()));
        _scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(scriptContextBuilder);

        _instanceExtensionService.ProcessExtensionsAsync(
                Arg.Any<string[]?>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<Definitions.Workflow>(),
                Arg.Any<ExtensionScope>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<Dictionary<string, object>>.Ok(new Dictionary<string, object>()));

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
            instanceExtensionService: _instanceExtensionService,
            scriptContextFactory: _scriptContextFactory,
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: Substitute.For<IUrlTemplateBuilder>(),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: _transitionAuthorizationManager,
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: _schemaFieldFilterService,
            currentUser: Substitute.For<ICurrentUser>(),
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: _dataFunctionCache,
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task GetInstanceDataAsync_WhenCacheDisabled_DoesNotTouchCacheOrFingerprint()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance);

        var result = await _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        _dataFunctionCache.DidNotReceive().BuildKey(Arg.Any<GetInstanceDataInput>());
        await _dataFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _dataFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.DataFunctionCacheEntry>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .GetDataFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceDataAsync_WhenIfNoneMatchMatchesFingerprintEtag_Returns304WithoutCacheOrBuild()
    {
        var instanceId = Guid.NewGuid();
        EnableCache();
        SetupFingerprint(instanceId);

        var input = CreateInput(instanceId.ToString());
        input.IfNoneMatch = "\"etag-current\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeTrue();
        await _dataFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceExtensionService.DidNotReceive().ProcessExtensionsAsync(
            Arg.Any<string[]?>(), Arg.Any<ScriptContext>(), Arg.Any<Definitions.Workflow>(),
            Arg.Any<ExtensionScope>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A validated cache entry supplies the DATA portion of the response — field filtering is
    /// skipped — while the extension pipeline still runs fresh on the loaded aggregate.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_WhenCachedEtagMatchesCurrent_UsesCachedDataAndBuildsExtensionsFresh()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance);
        EnableCache();
        SetupFingerprint(instance.Id);
        SetupCachedEntry(out _);
        _instanceExtensionService.ProcessExtensionsAsync(
                Arg.Any<string[]?>(), Arg.Any<ScriptContext>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<ExtensionScope>(), Arg.Any<CancellationToken>())
            .Returns(Result<Dictionary<string, object>>.Ok(new Dictionary<string, object> { ["state"] = "fresh" }));

        var result = await _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.IsNotModified.ShouldBeFalse();
        result.Result.IsSuccess.ShouldBeTrue();
        // Data from the cache entry, extensions freshly computed.
        result.Result.Value!.Data!.Value.GetProperty("key").GetString().ShouldBe("cached-value");
        result.Result.Value!.Extensions!["state"].ShouldBe("fresh");
        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
        // Field filtering skipped; entry not re-written.
        await _schemaFieldFilterService.DidNotReceive()
            .ApplyAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<System.Text.Json.JsonElement?>(),
                Arg.Any<Instance>(), Arg.Any<Authorization.AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>());
        await _dataFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.DataFunctionCacheEntry>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Extension demand does not bypass the data cache: the cached DATA portion is reused for
    /// extension-carrying requests too, and the requested extensions are computed fresh.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_WhenExtensionsRequested_StillUsesCachedDataWithFreshExtensions()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance);
        EnableCache();
        SetupFingerprint(instance.Id);
        SetupCachedEntry(out _);
        _instanceExtensionService.ProcessExtensionsAsync(
                Arg.Any<string[]?>(), Arg.Any<ScriptContext>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<ExtensionScope>(), Arg.Any<CancellationToken>())
            .Returns(Result<Dictionary<string, object>>.Ok(new Dictionary<string, object> { ["ext-a"] = "fresh" }));

        var input = CreateInput(instance.Id.ToString());
        input.Extensions = ["ext-a"];

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Data!.Value.GetProperty("key").GetString().ShouldBe("cached-value");
        result.Result.Value!.Extensions!["ext-a"].ShouldBe("fresh");
        await _dataFunctionCache.Received(1).GetAsync(TestCacheKey, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A matching If-None-Match answers 304 even for extension-demanding requests — the ETag
    /// tracks the data change point, extension flux never moves it.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_WhenExtensionsRequestedWithMatchingEtag_Returns304()
    {
        var instanceId = Guid.NewGuid();
        EnableCache();
        SetupFingerprint(instanceId);

        var input = CreateInput(instanceId.ToString());
        input.Extensions = ["ext-a"];
        input.IfNoneMatch = "\"etag-current\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeTrue();
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The entry stores only the field-filtered data — extension output (e.g. always-on Global
    /// extensions) is never written to the cache.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_WhenBuildProducesExtensionOutput_WarmsCacheWithDataOnly()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance);
        EnableCache();
        SetupFingerprint(instance.Id);
        _instanceExtensionService.ProcessExtensionsAsync(
                Arg.Any<string[]?>(), Arg.Any<ScriptContext>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<ExtensionScope>(), Arg.Any<CancellationToken>())
            .Returns(Result<Dictionary<string, object>>.Ok(new Dictionary<string, object> { ["state"] = "review" }));

        var result = await _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Extensions!["state"].ShouldBe("review");
        await _dataFunctionCache.Received(1).SetAsync(
            TestCacheKey,
            Arg.Is<Caching.DataFunctionCacheEntry>(e => e.Etag == "etag-current" && e.Data != null),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceDataAsync_WhenCacheMiss_BuildsAndWarmsCacheWithResolvedTtl()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance, workflowTtlSeconds: 120);
        EnableCache();
        SetupFingerprint(instance.Id);
        _dataFunctionCache
            .ResolveTtlSeconds(Arg.Is<FunctionCacheDefinition?>(f => f != null && f.TtlSeconds == 120))
            .Returns(120);

        var result = await _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
        await _dataFunctionCache.Received(1).SetAsync(
            TestCacheKey,
            Arg.Is<Caching.DataFunctionCacheEntry>(e => e.Etag == "etag-current"),
            120,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceDataAsync_WhenCachedEtagIsStale_RebuildsAndRefreshesCache()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance);
        EnableCache();
        SetupFingerprint(instance.Id);
        SetupCachedEntry(out _, etag: "etag-old");

        var result = await _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
        await _dataFunctionCache.Received(1).SetAsync(
            TestCacheKey,
            Arg.Is<Caching.DataFunctionCacheEntry>(e => e.Etag == "etag-current"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceDataAsync_WhenPinnedVersionRequested_BypassesFastPathAndCache()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance);
        _instanceRepository
            .FindByIdentifierWithFullHistoryAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);
        EnableCache();

        var input = CreateInput(instance.Id.ToString());
        input.Version = TestVersion;

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.DidNotReceive()
            .GetDataFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _dataFunctionCache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _dataFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.DataFunctionCacheEntry>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // ETag is still computed uniformly (from the resolved row) even off the fast path.
        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
    }

    [Fact]
    public async Task GetInstanceDataAsync_WhenInstanceHasNoDataRow_DoesNotWarmCache()
    {
        var instance = CreateInstanceWithoutData();
        SetupFullPathMocks(instance);
        EnableCache();
        SetupFingerprint(instance.Id, latestDataEtag: null);

        var result = await _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        await _dataFunctionCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<Caching.DataFunctionCacheEntry>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceDataAsync_WhenFingerprintNotFound_FallsThroughToFullPath()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance);
        EnableCache();
        _instanceRepository
            .GetDataFingerprintAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns((InstanceDataFingerprint?)null);

        var result = await _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void EnableCache()
    {
        _dataFunctionCache.Enabled.Returns(true);
        _dataFunctionCache.BuildKey(Arg.Any<GetInstanceDataInput>()).Returns(TestCacheKey);
        _dataFunctionCache
            .ComputeEtag(Arg.Any<GetInstanceDataInput>(), Arg.Any<InstanceDataFingerprint>())
            .Returns("etag-current");
    }

    private void SetupFingerprint(Guid instanceId, string? latestDataEtag = "01JD2G4YV0EXAMPLEULID0000A") =>
        _instanceRepository
            .GetDataFingerprintAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new InstanceDataFingerprint(instanceId, "test-key", latestDataEtag, TestVersion, "review", HasActiveSubFlow: false));

    private void SetupCachedEntry(out Caching.DataFunctionCacheEntry entry, string etag = "etag-current")
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "key": "cached-value" }""");
        entry = new Caching.DataFunctionCacheEntry
        {
            Etag = etag,
            EntityEtag = "entity-1",
            Data = doc.RootElement.Clone()
        };
        _dataFunctionCache.GetAsync(TestCacheKey, Arg.Any<CancellationToken>()).Returns(entry);
    }

    private static Instance CreateInstanceWithData(out Guid dataId)
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create("review", StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);
        dataId = Guid.NewGuid();
        instance.SeedDataWithVersion(dataId, new JsonData("{\"key\":\"value\"}"), TestVersion);
        return instance;
    }

    private static Instance CreateInstanceWithoutData()
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create("review", StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);
        return instance;
    }

    private void SetupFullPathMocks(Instance instance, int? workflowTtlSeconds = null)
    {
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);

        var functionCacheJson = workflowTtlSeconds is not null
            ? $$""", "config": { "functionCache": { "ttlSeconds": {{workflowTtlSeconds}} } }"""
            : string.Empty;
        var json = $$"""
                   {
                       "type": "F"{{functionCacheJson}},
                       "labels": [], "functions": [], "features": [], "states": [],
                       "sharedTransitions": [], "extensions": [],
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

        _componentCacheStore
            .GetFlowAsync(TestDomain, TestWorkflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
    }

    private static GetInstanceDataInput CreateInput(string instanceId) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instanceId,
        Headers = new Dictionary<string, string?>(),
        QueryParameters = new Dictionary<string, string?>()
    };
}
