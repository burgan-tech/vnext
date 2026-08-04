using System;
using System.Collections.Generic;
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
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
            urlTemplateBuilder: new StubUrlTemplateBuilder(),
            functionAccessPolicy: new FunctionAccessPolicy(
                Substitute.For<ICurrentUser>(), _authorizationManager),
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

    [Fact]
    public async Task Info_WhenRolesDeny_ReturnsForbidden_WithoutRevealingContracts()
    {
        SetupFunction(FunctionTestFactory.Attributes($$"""
            "roles": [ { "role": "ops", "grant": "allow" } ],
            "inputView": {{FunctionTestFactory.Ref("v1", "sys-views")}}
            """));
        _authorizationManager
            .IsAnyRoleAllowedForGrantsAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.GetInfoByKeyAsync(TestDomain, FunctionKey);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
    }

    [Fact]
    public async Task Info_OnTheDomainRoute_ForAFlowScopedFunction_IsForbidden()
    {
        SetupFunction(FunctionTestFactory.Attributes(scope: "F"));

        var result = await _service.GetInfoByKeyAsync(TestDomain, FunctionKey);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionScopeNotSatisfied);
    }

    [Fact]
    public async Task View_WhenRolesDeny_ReturnsForbidden()
    {
        SetupFunction(FunctionTestFactory.Attributes($$"""
            "roles": [ { "role": "ops", "grant": "allow" } ],
            "inputView": {{FunctionTestFactory.Ref("v1", "sys-views")}}
            """));
        _authorizationManager
            .IsAnyRoleAllowedForGrantsAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.GetViewByKeyAsync(TestDomain, FunctionKey, "input");

        result.IsSuccess.ShouldBeFalse();
        await _viewContentResolution.DidNotReceiveWithAnyArgs()
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
        info.Function.Href.ShouldBe($"/{TestDomain}/functions/{FunctionKey}");
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

        info.InputView.Href.ShouldBe($"/{TestDomain}/functions/{FunctionKey}/view?target=input");
        info.OutputView.Href.ShouldBe($"/{TestDomain}/functions/{FunctionKey}/view?target=output");
        info.InputSchema.Href.ShouldBe($"/{TestDomain}/functions/{FunctionKey}/schema?target=input");
        info.OutputSchema.Href.ShouldBe($"/{TestDomain}/functions/{FunctionKey}/schema?target=output");
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
            $"/{TestDomain}/workflows/{TestFlow}/instances/{instance.Id}/functions/{FunctionKey}");
        info.InputView.Href.ShouldBe(
            $"/{TestDomain}/workflows/{TestFlow}/instances/{instance.Id}/functions/{FunctionKey}/view?target=input");
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

    // ─── Setup helpers ──────────────────────────────────────────────────────────

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

    /// <summary>
    /// Emits the default templates verbatim so the tests assert the shape clients actually receive
    /// rather than a mock's placeholder.
    /// </summary>
    private sealed class StubUrlTemplateBuilder : IUrlTemplateBuilder
    {
        private readonly UrlTemplateOptions _options = new();

        public string BuildStartUrl(string domain, string workflow, string? apiVersionPrefix = null)
            => string.Format(_options.Start, domain, workflow);

        public string BuildTransitionUrl(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null)
            => string.Format(_options.Transition, domain, workflow, instanceId, transitionKey);

        public string BuildFunctionListUrl(string domain, string workflow, string function, string? apiVersionPrefix = null)
            => string.Format(_options.FunctionList, domain, workflow, function);

        public string BuildInstanceListUrl(string domain, string workflow, string? apiVersionPrefix = null)
            => string.Format(_options.InstanceList, domain, workflow);

        public string BuildInstanceUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
            => string.Format(_options.Instance, domain, workflow, instance);

        public string BuildInstanceHistoryUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
            => string.Format(_options.InstanceHistory, domain, workflow, instance);

        public string BuildDataUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
            => string.Format(_options.Data, domain, workflow, instance);

        public string BuildDataWithExtensionsUrl(string domain, string workflow, string instance, IEnumerable<string> extensions, string? apiVersionPrefix = null)
            => BuildDataUrl(domain, workflow, instance, apiVersionPrefix);

        public string BuildViewUrl(string domain, string workflow, string instance, string? transitionKey = null, string? apiVersionPrefix = null)
            => string.Format(_options.View, domain, workflow, instance);

        public string BuildSchemaUrl(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null)
            => string.Format(_options.Schema, domain, workflow, instanceId, transitionKey);

        public string BuildMasterUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
            => string.Format(_options.Master, domain, workflow, instance);

        public string BuildDomainFunctionUrl(string domain, string function, string? apiVersionPrefix = null)
            => string.Format(_options.DomainFunction, domain, function);

        public string BuildDomainFunctionInfoUrl(string domain, string function, string? apiVersionPrefix = null)
            => string.Format(_options.DomainFunctionInfo, domain, function);

        public string BuildDomainFunctionViewUrl(string domain, string function, string target, string? apiVersionPrefix = null)
            => string.Format(_options.DomainFunctionView, domain, function, target);

        public string BuildDomainFunctionSchemaUrl(string domain, string function, string target, string? apiVersionPrefix = null)
            => string.Format(_options.DomainFunctionSchema, domain, function, target);

        public string BuildInstanceFunctionUrl(string domain, string workflow, string instance, string function, string? apiVersionPrefix = null)
            => string.Format(_options.InstanceFunction, domain, workflow, instance, function);

        public string BuildInstanceFunctionInfoUrl(string domain, string workflow, string instance, string function, string? apiVersionPrefix = null)
            => string.Format(_options.InstanceFunctionInfo, domain, workflow, instance, function);

        public string BuildInstanceFunctionViewUrl(string domain, string workflow, string instance, string function, string target, string? apiVersionPrefix = null)
            => string.Format(_options.InstanceFunctionView, domain, workflow, instance, function, target);

        public string BuildInstanceFunctionSchemaUrl(string domain, string workflow, string instance, string function, string target, string? apiVersionPrefix = null)
            => string.Format(_options.InstanceFunctionSchema, domain, workflow, instance, function, target);
    }
}
