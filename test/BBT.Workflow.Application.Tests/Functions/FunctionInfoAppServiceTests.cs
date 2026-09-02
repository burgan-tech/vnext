using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Infrastructure.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionInfoAppService"/>: discovery is gated exactly like execution,
/// contract slots resolve through the shared resolver, and hyperlinks are emitted whether or not a
/// contract currently resolves.
/// </summary>
[Collection("AmbientServiceProvider")]
public sealed class FunctionInfoAppServiceTests : IDisposable
{
    private const string TestDomain = FunctionTestFactory.Domain;
    private const string TestVersion = FunctionTestFactory.Version;
    private const string FunctionKey = "my-fn";
    private const string TestFlow = "my-flow";

    /// <summary>
    /// The prefix the real builder emits when nothing is configured — the same one a client sees from a
    /// host that declares no <c>UrlTemplates</c> section.
    /// </summary>
    private const string BasePath = UrlTemplateDefaults.BasePath;

    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly ITaskConditionService _conditionService = Substitute.For<ITaskConditionService>();
    private readonly IViewContentResolutionService _viewContentResolution =
        Substitute.For<IViewContentResolutionService>();
    private readonly ITransitionAuthorizationManager _authorizationManager =
        Substitute.For<ITransitionAuthorizationManager>();

    private readonly ServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;
    private readonly FunctionInfoAppService _service;

    public FunctionInfoAppServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<ILazyServiceProvider, LazyServiceProvider>();
        _ambientServiceProvider = services.BuildServiceProvider();
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithWorkflow(Arg.Any<Definitions.Workflow?>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance?>()).Returns(builder);
        builder.WithBody(Arg.Any<object?>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.WithQueryParameters(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>())
            .Returns(new ScriptContext(NullLogger<ScriptContext>.Instance));

        var scriptContextFactory = Substitute.For<IScriptContextFactory>();
        scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);

        _service = new FunctionInfoAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            instanceRepository: _instanceRepository,
            scriptContextFactory: scriptContextFactory,
            componentCacheStore: _componentCacheStore,
            currentSchema: Substitute.For<ICurrentSchema>(),
            urlTemplateBuilder: new UrlTemplateBuilder(Options.Create(new UrlTemplateOptions())),
            functionAccessPolicy: new FunctionAccessPolicy(),
            contractResolver: new FunctionContractResolver(
                _conditionService, NullLogger<FunctionContractResolver>.Instance),
            viewContentResolutionService: _viewContentResolution);
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        _ambientServiceProvider.Dispose();
    }

    // ─── Access gates ───────────────────────────────────────────────────────────

    /// <summary>
    /// <c>function.roles</c> no longer gates the runtime's own surfaces — the middle tier owns that
    /// decision and consults the <c>authorize</c> function for it. Info therefore reports the contract
    /// of a roles-bearing function to a caller who holds none of them.
    /// </summary>
    [Fact]
    public async Task Info_IsNotRoleGated()
    {
        SetupFunction(FunctionTestFactory.Attributes($$"""
            "roles": [ { "role": "ops", "grant": "allow" } ],
            "inputView": {{FunctionTestFactory.Ref("v1", "sys-views")}}
            """));

        var result = await _service.GetInfoByKeyAsync(TestDomain, FunctionKey);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Info_OnTheDomainRoute_ForAFlowScopedFunction_IsForbidden()
    {
        SetupFunction(FunctionTestFactory.Attributes(scope: "F"));

        var result = await _service.GetInfoByKeyAsync(TestDomain, FunctionKey);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionScopeNotSatisfied);
    }

    /// <summary>
    /// Same as <see cref="Info_IsNotRoleGated"/> for the view slot. The call now reaches view-content
    /// resolution instead of being short-circuited by the role gate — that reach is the assertion,
    /// since resolution itself is stubbed out here.
    /// </summary>
    [Fact]
    public async Task View_IsNotRoleGated()
    {
        SetupFunction(FunctionTestFactory.Attributes($$"""
            "roles": [ { "role": "ops", "grant": "allow" } ],
            "inputView": {{FunctionTestFactory.Ref("v1", "sys-views")}}
            """));

        await _service.GetViewByKeyAsync(TestDomain, FunctionKey, "input");

        await _viewContentResolution.ReceivedWithAnyArgs(1)
            .ResolveViewContentAsync(default!, default!, default, default, default);
    }

    // ─── Info payload ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Info_ReportsMetaAndTheExecutableHref()
    {
        SetupFunction(FunctionTestFactory.Attributes("""
            "verbs": ["POST", "PATCH"],
            "rawResponse": true
            """));

        var result = await _service.GetInfoByKeyAsync(TestDomain, FunctionKey);

        var info = result.Value.ShouldNotBeNull();
        info.Key.ShouldBe(FunctionKey);
        info.Domain.ShouldBe(TestDomain);
        info.Version.ShouldBe(TestVersion);
        info.Scope.ShouldBe("D");
        info.RawResponse.ShouldBeTrue();
        info.Cacheable.ShouldBeFalse();
        info.Function.Verbs.ShouldBe(["POST", "PATCH"]);
        info.Function.Href.ShouldBe($"{BasePath}/{TestDomain}/functions/{FunctionKey}");
    }

    [Fact]
    public async Task Info_UndeclaredContracts_StillEmitHrefs_WithHasFlagsFalse()
    {
        SetupFunction(FunctionTestFactory.Attributes());

        var info = (await _service.GetInfoByKeyAsync(TestDomain, FunctionKey)).Value.ShouldNotBeNull();

        info.InputView.HasView.ShouldBeFalse();
        info.OutputView.HasView.ShouldBeFalse();
        info.InputSchema.HasSchema.ShouldBeFalse();
        info.OutputSchema.HasSchema.ShouldBeFalse();

        info.InputView.Href.ShouldBe($"{BasePath}/{TestDomain}/functions/{FunctionKey}/view?target=input");
        info.OutputView.Href.ShouldBe($"{BasePath}/{TestDomain}/functions/{FunctionKey}/view?target=output");
        info.InputSchema.Href.ShouldBe($"{BasePath}/{TestDomain}/functions/{FunctionKey}/schema?target=input");
        info.OutputSchema.Href.ShouldBe($"{BasePath}/{TestDomain}/functions/{FunctionKey}/schema?target=output");
    }

    [Fact]
    public async Task Info_DeclaredContracts_SetTheHasFlags()
    {
        SetupFunction(FunctionTestFactory.Attributes($$"""
            "inputView":    {{FunctionTestFactory.Ref("v1", "sys-views")}},
            "outputView":   {{FunctionTestFactory.Ref("v2", "sys-views")}},
            "inputSchema":  {{FunctionTestFactory.Ref("s1", "sys-schemas")}},
            "outputSchema": {{FunctionTestFactory.Ref("s2", "sys-schemas")}}
            """));

        var info = (await _service.GetInfoByKeyAsync(TestDomain, FunctionKey)).Value.ShouldNotBeNull();

        info.InputView.HasView.ShouldBeTrue();
        info.OutputView.HasView.ShouldBeTrue();
        info.InputSchema.HasSchema.ShouldBeTrue();
        info.OutputSchema.HasSchema.ShouldBeTrue();
    }

    [Fact]
    public async Task Info_RuleBasedViewThatMatchesNothing_ReportsNoViewButKeepsTheHref()
    {
        SetupFunction(FunctionTestFactory.Attributes($$"""
            "inputView": [
                { "rule": {{FunctionTestFactory.Rule("false")}}, "view": {{FunctionTestFactory.Ref("v1", "sys-views")}} }
            ]
            """));
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(false));

        var info = (await _service.GetInfoByKeyAsync(TestDomain, FunctionKey)).Value.ShouldNotBeNull();

        info.InputView.HasView.ShouldBeFalse();
        info.InputView.Href.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Info_PropagatesLoadDataFromTheWinningViewEntry()
    {
        SetupFunction(FunctionTestFactory.Attributes($$"""
            "inputView": [
                { "view": {{FunctionTestFactory.Ref("v1", "sys-views")}}, "loadData": true }
            ]
            """));

        var info = (await _service.GetInfoByKeyAsync(TestDomain, FunctionKey)).Value.ShouldNotBeNull();

        info.InputView.LoadData.ShouldBeTrue();
    }

    [Fact]
    public async Task Info_ByInstance_UsesInstanceScopedHrefs()
    {
        var instance = SetupInstance();
        SetupFlow();
        SetupFunction(FunctionTestFactory.Attributes(scope: "I"));

        var info = (await _service.GetInfoByInstanceAsync(
            TestDomain, TestFlow, instance.Id.ToString(), FunctionKey)).Value.ShouldNotBeNull();

        info.Function.Href.ShouldBe(
            $"{BasePath}/{TestDomain}/workflows/{TestFlow}/instances/{instance.Id}/functions/{FunctionKey}");
        info.InputView.Href.ShouldBe(
            $"{BasePath}/{TestDomain}/workflows/{TestFlow}/instances/{instance.Id}/functions/{FunctionKey}/view?target=input");
    }

    // ─── Content endpoints ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("sideways")]
    [InlineData("INPUTS")]
    public async Task View_UnknownTarget_IsRejected(string target)
    {
        SetupFunction(FunctionTestFactory.Attributes());

        var result = await _service.GetViewByKeyAsync(TestDomain, FunctionKey, target);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionContractTargetInvalid);
    }

    [Fact]
    public async Task View_BlankTarget_DefaultsToInput()
    {
        SetupFunction(FunctionTestFactory.Attributes(
            $$""" "inputView": {{FunctionTestFactory.Ref("v1", "sys-views")}} """));
        _viewContentResolution
            .ResolveViewContentAsync(
                Arg.Any<IReference>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string?>?>(),
                Arg.Any<Dictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<GetViewOutput>.Ok(new GetViewOutput { Key = "v1" }));

        var result = await _service.GetViewByKeyAsync(TestDomain, FunctionKey, string.Empty);

        result.IsSuccess.ShouldBeTrue();
        await _viewContentResolution.Received(1).ResolveViewContentAsync(
            Arg.Is<IReference>(r => r.Key == "v1"), TestDomain,
            Arg.Any<Dictionary<string, string?>?>(), Arg.Any<Dictionary<string, string?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task View_UndeclaredSlot_IsNotFound()
    {
        SetupFunction(FunctionTestFactory.Attributes());

        var result = await _service.GetViewByKeyAsync(TestDomain, FunctionKey, "output");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionContractNotResolved);
    }

    [Fact]
    public async Task Schema_ReturnsTheResolvedSchemaDocument()
    {
        SetupFunction(FunctionTestFactory.Attributes(
            $$""" "outputSchema": {{FunctionTestFactory.Ref("s2", "sys-schemas")}} """));
        SetupSchema("s2");

        var result = await _service.GetSchemaByKeyAsync(TestDomain, FunctionKey, "output");

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Key.ShouldBe("s2");
        result.Value.Type.ShouldBe("json");
    }

    // ─── Catalog ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Catalog_WhenWorkflowDeclaresNoFunctions_IsEmpty()
    {
        var instance = SetupInstance();
        SetupFlowWithFunctions();

        var result = await _service.GetCatalogByInstanceAsync(TestDomain, TestFlow, instance.Id.ToString());

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Functions.ShouldBeEmpty();
    }

    /// <summary>
    /// The href must match the function's scope: the domain route rejects Flow- and Instance-scoped
    /// functions with 403, so linking them there would hand the client a dead link.
    /// </summary>
    [Theory]
    [InlineData("D", false)]
    [InlineData("F", true)]
    [InlineData("I", true)]
    public async Task Catalog_BuildsTheInfoHrefFromTheFunctionScope(string scope, bool instanceScoped)
    {
        var instance = SetupInstance();
        SetupFlowWithFunctions("get-branches");
        SetupCatalogFunction("get-branches", scope);

        var result = await _service.GetCatalogByInstanceAsync(TestDomain, TestFlow, instance.Id.ToString());

        var entry = result.Value.ShouldNotBeNull().Functions.ShouldHaveSingleItem();
        entry.Name.ShouldBe("get-branches");
        entry.Version.ShouldBe(TestVersion);
        entry.Scope.ShouldBe(scope);
        entry.Href.ShouldBe(instanceScoped
            ? $"{BasePath}/{TestDomain}/workflows/{TestFlow}/instances/{instance.Id}/functions/get-branches/info"
            : $"{BasePath}/{TestDomain}/functions/get-branches/info");
    }

    /// <summary>
    /// The catalog is scope-filtered, not role-filtered. A roles-bearing function is advertised to a
    /// caller holding none of its roles — deciding whether to surface the link is the middle tier's
    /// call, made against the <c>authorize</c> function.
    /// </summary>
    [Fact]
    public async Task Catalog_IsNotRoleFiltered()
    {
        var instance = SetupInstance();
        SetupFlowWithFunctions("open-fn", "guarded-fn");
        SetupCatalogFunction("open-fn", "I");
        SetupCatalogFunction("guarded-fn", "I",
            roles: """, "roles": [ { "role": "ops", "grant": "allow" } ]""");

        var result = await _service.GetCatalogByInstanceAsync(TestDomain, TestFlow, instance.Id.ToString());

        result.Value!.Functions.Select(f => f.Name).ShouldBe(["open-fn", "guarded-fn"], ignoreOrder: true);
    }

    /// <summary>
    /// A broken function reference is omitted rather than failing the whole catalog.
    /// </summary>
    [Fact]
    public async Task Catalog_SkipsUnresolvableReferences()
    {
        var instance = SetupInstance();
        SetupFlowWithFunctions("missing-fn", "get-branches");
        _componentCacheStore
            .GetFunctionAsync(TestDomain, "missing-fn", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Function>.Fail(Error.NotFound("fn.notfound", "gone")));
        SetupCatalogFunction("get-branches", "I");

        var result = await _service.GetCatalogByInstanceAsync(TestDomain, TestFlow, instance.Id.ToString());

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Functions.Select(f => f.Name).ShouldBe(["get-branches"]);
    }

    [Fact]
    public async Task Catalog_PreservesDeclarationOrder()
    {
        var instance = SetupInstance();
        SetupFlowWithFunctions("first-fn", "second-fn");
        SetupCatalogFunction("first-fn", "D");
        SetupCatalogFunction("second-fn", "I");

        var result = await _service.GetCatalogByInstanceAsync(TestDomain, TestFlow, instance.Id.ToString());

        result.Value!.Functions.Select(f => f.Name).ShouldBe(["first-fn", "second-fn"]);
    }

    [Fact]
    public async Task Catalog_WhenInstanceIsMissing_IsNotFound()
    {
        _instanceRepository
            .FindByIdentifierAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await _service.GetCatalogByInstanceAsync(TestDomain, TestFlow, "nope");

        result.IsSuccess.ShouldBeFalse();
    }

    // ─── Setup helpers ──────────────────────────────────────────────────────────

    /// <summary>Registers a flow declaring the given function keys, in order.</summary>
    private void SetupFlowWithFunctions(params string[] functionKeys)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestFlow, TestDomain, "sys-flows", TestVersion));
        workflow.SetType("F");
        foreach (var key in functionKeys)
            workflow.AddFunction(new Reference(key, TestDomain, "sys-functions", TestVersion));

        _componentCacheStore
            .GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
    }

    /// <summary>Registers a single function component keyed by name, so a catalog can span several.</summary>
    private void SetupCatalogFunction(string key, string scope, string roles = "")
    {
        var function = FunctionTestFactory.FromJson(
            FunctionTestFactory.Attributes(roles.TrimStart(',', ' '), scope: scope), key);

        _componentCacheStore
            .GetFunctionAsync(TestDomain, key, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Function>.Ok(function));
    }

    private void SetupFunction(string attributesJson)
    {
        var function = FunctionTestFactory.FromJson(attributesJson, FunctionKey);
        _componentCacheStore
            .GetFunctionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Function>.Ok(function));
    }

    private Instance SetupInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), TestFlow, TestVersion, "test-key");
        _instanceRepository
            .FindByIdentifierAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(instance);
        return instance;
    }

    private void SetupFlow()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestFlow, TestDomain, "sys-flows", TestVersion));
        workflow.SetType("F");
        workflow.AddFunction(new Reference(FunctionKey, TestDomain, "sys-functions", TestVersion));

        _componentCacheStore
            .GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
    }

    private void SetupSchema(string key)
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            """{"type":"json","schema":{"type":"object"}}""",
            JsonSerializerConstants.JsonOptions)!;
        schema.SetReference(new Reference(key, TestDomain, "sys-schemas", TestVersion));

        _componentCacheStore
            .GetSchemaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
    }
}
