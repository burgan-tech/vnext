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
using BBT.Workflow.Definitions.Schemas;
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

using BBT.Workflow.Instances.HumanTask;

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
    private readonly IInstanceQueryGateway _instanceQueryGateway = Substitute.For<IInstanceQueryGateway>();
    private readonly IInstanceCorrelationRepository _instanceCorrelationRepository = Substitute.For<IInstanceCorrelationRepository>();
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
            instanceCorrelationRepository: _instanceCorrelationRepository,
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            instanceIncidentRepository: Substitute.For<IInstanceIncidentRepository>(),
            instanceTaskRepository: Substitute.For<IInstanceTaskRepository>(),
            instanceActionRepository: Substitute.For<IInstanceActionRepository>(),
            longPollInteractionGate: NSubstitute.Substitute.For<BBT.Workflow.Execution.LongPoll.ILongPollInteractionGate>(),
            instanceExtensionService: _instanceExtensionService,
            scriptContextFactory: _scriptContextFactory,
            instanceQueryGateway: _instanceQueryGateway,
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: Substitute.For<IUrlTemplateBuilder>(),
            instanceCorrelationResolver: Substitute.For<BBT.Workflow.Instances.Correlation.IInstanceCorrelationResolver>(),
            correlationOptions: Microsoft.Extensions.Options.Options.Create(new BBT.Workflow.Instances.Correlation.InstanceCorrelationOptions()),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: _transitionAuthorizationManager,
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new BBT.Workflow.Instances.InstanceDataReadService(_schemaFieldFilterService),
            callerRoleResolver: new DefaultCallerRoleResolver(Substitute.For<ICurrentUser>()),
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(new HumanTaskFunctionOptions()),
            attributeIndexCatalog: Substitute.For<IAttributeIndexCatalog>(),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: _dataFunctionCache,
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),

            humanTaskFunctionCache: Substitute.For<Caching.IHumanTaskFunctionCache>(),
            currentUser: Substitute.For<ICurrentUser>(),
            descentLimiter: new HumanTask.HumanTaskDescentLimiter(
                Microsoft.Extensions.Options.Options.Create(new HumanTask.HumanTaskFunctionOptions())),
            transitionSchemaResolver: NSubstitute.Substitute.For<BBT.Workflow.Execution.Validation.ITransitionSchemaResolver>(),
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
    /// The production scenario the two identities exist for: a transition moved the state without
    /// writing data, and the caller presents the ETag it got before the hop. The representation
    /// ETag has moved, so this is NOT a 304 — the caller must receive the new extension output. The
    /// body, however, is unchanged, so the cached payload is still served rather than rebuilt: the
    /// correctness fix must not cost the body cache.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_WhenOnlyStateMovedAndCallerHoldsPreHopEtag_Returns200FromCachedBody()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance);
        EnableCache();
        SetupFingerprint(instance.Id);
        // Entry stamped with the data identity — untouched by the hop, so it stays valid.
        SetupCachedEntry(out _);
        _instanceExtensionService.ProcessExtensionsAsync(
                Arg.Any<string[]?>(), Arg.Any<ScriptContext>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<ExtensionScope>(), Arg.Any<CancellationToken>())
            .Returns(Result<Dictionary<string, object>>.Ok(new Dictionary<string, object> { ["state"] = "after-hop" }));

        var input = CreateInput(instance.Id.ToString());
        input.IfNoneMatch = "\"etag-before-hop\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        // Not a 304: the state moved, so the enrichment the caller holds is stale.
        result.IsNotModified.ShouldBeFalse();
        result.Result.Value!.Extensions!["state"].ShouldBe("after-hop");
        // ...but the body came from the cache, and the filtering pass was skipped.
        result.Result.Value!.Data!.Value.GetProperty("key").GetString().ShouldBe("cached-value");
        await _dataFunctionCache.Received(1).GetAsync(TestCacheKey, Arg.Any<CancellationToken>());
        await _schemaFieldFilterService.DidNotReceive()
            .ApplyAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<System.Text.Json.JsonElement?>(),
                Arg.Any<Instance>(), Arg.Any<Authorization.AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>());
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
            Arg.Is<Caching.DataFunctionCacheEntry>(e => e.Etag == "data-etag-current" && e.Data != null),
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
            Arg.Is<Caching.DataFunctionCacheEntry>(e => e.Etag == "data-etag-current"),
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
            Arg.Is<Caching.DataFunctionCacheEntry>(e => e.Etag == "data-etag-current"),
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

    // ── Active SubFlow (#1084) ────────────────────────────────────────────────

    /// <summary>
    /// An unchanged chain: the parent part matches and the child answers 304 to the child part, so
    /// the parent answers 304 without loading its aggregate or running any extension.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_ActiveSubFlow_ChildUnchanged_Returns304WithoutBuild()
    {
        var parentId = Guid.NewGuid();
        var correlation = CreateSubFlowCorrelation(parentId);
        EnableCache();
        SetupFingerprint(parentId, hasActiveSubFlow: true);
        _instanceCorrelationRepository.FindActiveSubFlowByParentAsReadOnlyAsync(parentId, Arg.Any<CancellationToken>())
            .Returns(correlation);
        _instanceQueryGateway.GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceDataOutput>.NotModified());

        var input = CreateInput(parentId.ToString());
        input.IfNoneMatch = "\"etag-current.child-1\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeTrue();
        await _instanceQueryGateway.Received(1).GetInstanceDataAsync(
            Arg.Is<GetInstanceDataInput>(i =>
                i.Instance == correlation.SubFlowInstanceId.ToString()
                && i.Domain == correlation.SubFlowDomain
                && i.Workflow == correlation.SubFlowName
                && i.IfNoneMatch == "child-1"
                && i.Version == null),
            Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The caller's own conditional headers never reach the child: the remote client merges the input
    /// headers over the request, so a forwarded <c>If-None-Match: "parent.child"</c> would replace the
    /// child part and the child could never answer 304 (found on the cross-domain lab).
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_ActiveSubFlow_DoesNotForwardTheCallersConditionalHeaders()
    {
        var parentId = Guid.NewGuid();
        var correlation = CreateSubFlowCorrelation(parentId);
        EnableCache();
        SetupFingerprint(parentId, hasActiveSubFlow: true);
        _instanceCorrelationRepository.FindActiveSubFlowByParentAsReadOnlyAsync(parentId, Arg.Any<CancellationToken>())
            .Returns(correlation);
        _instanceQueryGateway.GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceDataOutput>.NotModified());

        var input = CreateInput(parentId.ToString());
        input.IfNoneMatch = "\"etag-current.child-1\"";
        input.Headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["if-none-match"] = "\"etag-current.child-1\"",
            ["If-Modified-Since"] = "Wed, 18 Nov 2026 10:00:00 GMT",
            ["sub"] = "caller-1"
        };

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeTrue();
        await _instanceQueryGateway.Received(1).GetInstanceDataAsync(
            Arg.Is<GetInstanceDataInput>(i =>
                i.IfNoneMatch == "child-1"
                && !i.Headers!.ContainsKey("If-None-Match")
                && !i.Headers.ContainsKey("If-Modified-Since")
                && i.Headers["sub"] == "caller-1"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The #1084 bug: the parent part still matches, but the child's data moved. The child answers
    /// with a body, the parent serves the child's new extensions under a new composed ETag, and the
    /// child is read once — the build reuses the fast path's read.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_ActiveSubFlow_ChildChanged_Returns200WithChildExtensionsAndComposedEtag()
    {
        var instance = CreateInstanceWithData(out _);
        var correlation = AddActiveSubFlow(instance);
        EnableCache();
        SetupFingerprint(instance.Id, hasActiveSubFlow: true);
        SetupFullPathMocks(instance);
        _instanceCorrelationRepository.FindActiveSubFlowByParentAsReadOnlyAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(correlation);
        SetupChildData("child-2", new Dictionary<string, object> { ["otpInput"] = "wrong-otp" });

        var input = CreateInput(instance.Id.ToString());
        input.IfNoneMatch = "\"etag-current.child-1\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeFalse();
        result.Result.Value!.ETag.ShouldBe("\"etag-current.child-2\"");
        result.Result.Value.Extensions!["otpInput"].ShouldBe("wrong-otp");
        await _instanceQueryGateway.Received(1)
            .GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>());
        await _instanceExtensionService.DidNotReceive().ProcessExtensionsAsync(
            Arg.Any<string[]?>(), Arg.Any<ScriptContext>(), Arg.Any<Definitions.Workflow>(),
            Arg.Any<ExtensionScope>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Nested SubFlow, unchanged: the child itself waits on a grandchild, so the caller holds
    /// <c>parent.child.grand</c>. The child is handed everything after the parent part
    /// (<c>child.grand</c>) and its 304 is the parent's 304.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_NestedSubFlow_ChainUnchanged_ForwardsChildAndGrandchildPartsAndReturns304()
    {
        var parentId = Guid.NewGuid();
        var correlation = CreateSubFlowCorrelation(parentId);
        EnableCache();
        SetupFingerprint(parentId, hasActiveSubFlow: true);
        _instanceCorrelationRepository.FindActiveSubFlowByParentAsReadOnlyAsync(parentId, Arg.Any<CancellationToken>())
            .Returns(correlation);
        _instanceQueryGateway.GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceDataOutput>.NotModified());

        var input = CreateInput(parentId.ToString());
        input.IfNoneMatch = "\"etag-current.child-1.grand-1\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeTrue();
        await _instanceQueryGateway.Received(1).GetInstanceDataAsync(
            Arg.Is<GetInstanceDataInput>(i => i.IfNoneMatch == "child-1.grand-1"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Nested SubFlow, grandchild moved: the child answers with its own composed ETag
    /// (<c>child.grand</c>) and the grandchild's extensions; the parent serves them under
    /// <c>parent.child.grand</c>, so a change two levels down still reaches the root's poller.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_NestedSubFlow_GrandchildChanged_ServesTheWholeChainEtag()
    {
        var instance = CreateInstanceWithData(out _);
        var correlation = AddActiveSubFlow(instance);
        EnableCache();
        SetupFingerprint(instance.Id, hasActiveSubFlow: true);
        SetupFullPathMocks(instance);
        _instanceCorrelationRepository.FindActiveSubFlowByParentAsReadOnlyAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(correlation);
        SetupChildData("child-1.grand-2", new Dictionary<string, object> { ["leaf"] = "grand-2" });

        var input = CreateInput(instance.Id.ToString());
        input.IfNoneMatch = "\"etag-current.child-1.grand-1\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeFalse();
        result.Result.Value!.ETag.ShouldBe("\"etag-current.child-1.grand-2\"");
        result.Result.Value.Extensions!["leaf"].ShouldBe("grand-2");
    }

    /// <summary>
    /// A plain parent-only ETag (issued before the SubFlow opened, or by an older runtime) is no longer
    /// a 304 on its own: the child is asked without a condition and the composed ETag is served.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_ActiveSubFlow_ParentOnlyEtag_IsNot304AndChildIsAskedUnconditionally()
    {
        var instance = CreateInstanceWithData(out _);
        var correlation = AddActiveSubFlow(instance);
        EnableCache();
        SetupFingerprint(instance.Id, hasActiveSubFlow: true);
        SetupFullPathMocks(instance);
        _instanceCorrelationRepository.FindActiveSubFlowByParentAsReadOnlyAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(correlation);
        SetupChildData("child-1", new Dictionary<string, object>());

        var input = CreateInput(instance.Id.ToString());
        input.IfNoneMatch = "\"etag-current\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeFalse();
        result.Result.Value!.ETag.ShouldBe("\"etag-current.child-1\"");
        await _instanceQueryGateway.Received(1).GetInstanceDataAsync(
            Arg.Is<GetInstanceDataInput>(i => i.IfNoneMatch == null), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The composed ETag decides the 304 on the build path too (cache disabled ⇒ no fast path).
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_ActiveSubFlow_CacheDisabled_MatchingComposedEtag_Returns304()
    {
        var instance = CreateInstanceWithData(out _);
        AddActiveSubFlow(instance);
        SetupFullPathMocks(instance);
        _dataFunctionCache
            .ComputeEtag(Arg.Any<GetInstanceDataInput>(), Arg.Any<InstanceDataFingerprint>())
            .Returns("etag-current");
        SetupChildData("child-1", new Dictionary<string, object>());

        var input = CreateInput(instance.Id.ToString());
        input.IfNoneMatch = "\"etag-current.child-1\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeTrue();
        await _instanceRepository.DidNotReceive()
            .GetDataFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A failed child read keeps the old behaviour (parent data, empty extensions) but gives the body no
    /// ETag, so a client can never pin the empty extensions block behind a 304.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_ActiveSubFlow_ChildReadFails_ServesWithoutEtagAndNever304()
    {
        var instance = CreateInstanceWithData(out _);
        AddActiveSubFlow(instance);
        SetupFullPathMocks(instance);
        _dataFunctionCache
            .ComputeEtag(Arg.Any<GetInstanceDataInput>(), Arg.Any<InstanceDataFingerprint>())
            .Returns("etag-current");
        _instanceQueryGateway.GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceDataOutput>.Fail(Error.Transient("remote_network_error", "down")));

        var input = CreateInput(instance.Id.ToString());
        input.IfNoneMatch = "\"etag-current\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeFalse();
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.ETag.ShouldBeNull();
        result.Result.Value.Extensions!.ShouldBeEmpty();
    }

    /// <summary>
    /// A child that answers with a body but no ETag (its own grandchild read failed, or a cross-domain
    /// child on an older runtime) leaves the child part unknown: its extensions are served, but the body
    /// gets no ETag, so the parent part alone never turns into a 304 while the child moves.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_ActiveSubFlow_ChildServesNoEtag_ServesWithoutEtagAndNever304()
    {
        var instance = CreateInstanceWithData(out _);
        AddActiveSubFlow(instance);
        SetupFullPathMocks(instance);
        _dataFunctionCache
            .ComputeEtag(Arg.Any<GetInstanceDataInput>(), Arg.Any<InstanceDataFingerprint>())
            .Returns("etag-current");
        SetupChildData(null, new Dictionary<string, object> { ["otpInput"] = "wrong-otp" });

        var input = CreateInput(instance.Id.ToString());
        input.IfNoneMatch = "\"etag-current\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeFalse();
        result.Result.Value!.ETag.ShouldBeNull();
        result.Result.Value.Extensions!["otpInput"].ShouldBe("wrong-otp");
    }

    /// <summary>
    /// The in-process gateway runs the child's read pipeline, so a child-side fault arrives as an
    /// exception. It takes the failed-read fallback (parent data, empty extensions, no ETag), not a 500.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_ActiveSubFlow_ChildReadThrows_ServesWithoutEtag()
    {
        var instance = CreateInstanceWithData(out _);
        var correlation = AddActiveSubFlow(instance);
        EnableCache();
        SetupFingerprint(instance.Id, hasActiveSubFlow: true);
        SetupFullPathMocks(instance);
        _instanceCorrelationRepository.FindActiveSubFlowByParentAsReadOnlyAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(correlation);
        _instanceQueryGateway.GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>())
            .Returns<ConditionalResult<GetInstanceDataOutput>>(_ => throw new TimeoutException("db timeout"));

        var input = CreateInput(instance.Id.ToString());
        input.IfNoneMatch = "\"etag-current.child-1\"";

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeFalse();
        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.ETag.ShouldBeNull();
        result.Result.Value.Extensions!.ShouldBeEmpty();
    }

    /// <summary>
    /// The caller's cancellation propagates, whether the gateway throws it or (remote client) reports it
    /// as a failed result — no fallback body is built for a request nobody waits on.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_ActiveSubFlow_CallerCancelled_Propagates()
    {
        var instance = CreateInstanceWithData(out _);
        AddActiveSubFlow(instance);
        SetupFullPathMocks(instance);
        _dataFunctionCache
            .ComputeEtag(Arg.Any<GetInstanceDataInput>(), Arg.Any<InstanceDataFingerprint>())
            .Returns("etag-current");
        using var cts = new CancellationTokenSource();
        _instanceQueryGateway.GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return ConditionalResult<GetInstanceDataOutput>.Fail(Error.Transient("remote_network_error", "cancelled"));
            });

        await Should.ThrowAsync<OperationCanceledException>(
            () => _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), cts.Token));
    }

    /// <summary>
    /// Regression guard for the hot path: without an active SubFlow nothing new is queried or called.
    /// </summary>
    [Fact]
    public async Task GetInstanceDataAsync_NoActiveSubFlow_NeverQueriesCorrelationOrChild()
    {
        var instance = CreateInstanceWithData(out _);
        EnableCache();
        SetupFingerprint(instance.Id);
        SetupFullPathMocks(instance);

        var result = await _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.Result.Value!.ETag.ShouldBe("\"etag-current\"");
        await _instanceCorrelationRepository.DidNotReceive()
            .FindActiveSubFlowByParentAsReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _instanceQueryGateway.DidNotReceive()
            .GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static InstanceCorrelation CreateSubFlowCorrelation(Guid parentId) =>
        InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: parentId,
            parentState: "review",
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: "S",
            subFlowDomain: "sub-domain",
            subFlowName: "sub-flow",
            subFlowVersion: TestVersion);

    private static InstanceCorrelation AddActiveSubFlow(Instance instance)
    {
        var correlation = CreateSubFlowCorrelation(instance.Id);
        instance.AddCorrelation(correlation);
        return correlation;
    }

    private void SetupChildData(string? childEtag, Dictionary<string, object> extensions) =>
        _instanceQueryGateway.GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceDataOutput>.Success(new GetInstanceDataOutput
            {
                ETag = childEtag,
                Extensions = extensions
            }));

    private void EnableCache()
    {
        _dataFunctionCache.Enabled.Returns(true);
        _dataFunctionCache.BuildKey(Arg.Any<GetInstanceDataInput>()).Returns(TestCacheKey);
        // Two identities, deliberately different values: the representation ETag drives the 304
        // decision, the data ETag stamps and validates the cached body (see IDataFunctionCache).
        _dataFunctionCache
            .ComputeEtag(Arg.Any<GetInstanceDataInput>(), Arg.Any<InstanceDataFingerprint>())
            .Returns("etag-current");
        _dataFunctionCache
            .ComputeDataEtag(Arg.Any<GetInstanceDataInput>(), Arg.Any<InstanceDataFingerprint>())
            .Returns("data-etag-current");
    }

    private void SetupFingerprint(
        Guid instanceId, string? latestDataEtag = "01JD2G4YV0EXAMPLEULID0000A", bool hasActiveSubFlow = false) =>
        _instanceRepository
            .GetDataFingerprintAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new InstanceDataFingerprint(instanceId, "test-key", latestDataEtag, TestVersion, "review", hasActiveSubFlow));

    private void SetupCachedEntry(out Caching.DataFunctionCacheEntry entry, string etag = "data-etag-current")
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

    /// <summary>
    /// A system read (trigger task) is served under the engine's own identity: it never consults the
    /// fingerprint, never reads or writes the caller-scoped cache and never runs the exposure pass, so a
    /// task copying a field gets the stored value and no raw body lands under a caller hash.
    /// </summary>
    /// <summary>A caller read with identical content still goes through the exposure pass.</summary>
    [Fact]
    public async Task GetInstanceDataAsync_WhenNotSystemRead_RunsTheExposurePass()
    {
        var instance = CreateInstanceWithData(out _);
        SetupFullPathMocks(instance);

        await _service.GetInstanceDataAsync(CreateInput(instance.Id.ToString()), CancellationToken.None);

        await _schemaFieldFilterService.Received(1)
            .ApplyAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<System.Text.Json.JsonElement?>(),
                Arg.Any<Instance>(), Arg.Any<Authorization.AuthorizationRequestContext?>(), Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyDictionary<string, string>?>());
    }
}
