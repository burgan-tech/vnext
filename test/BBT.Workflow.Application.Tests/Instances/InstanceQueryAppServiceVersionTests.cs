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
/// Tests that version-specific queries correctly load full DataList
/// instead of only IsLatest rows, preventing silent null returns
/// for non-latest versions.
/// </summary>
public class InstanceQueryAppServiceVersionTests : IDisposable
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly IComponentCacheStore _componentCacheStore;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IInstanceExtensionService _instanceExtensionService;
    private readonly IScriptContextFactory _scriptContextFactory;
    private readonly IRepresentationEtagService _representationEtagService;
    private readonly ISchemaFieldFilterService _schemaFieldFilterService;
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string LatestVersion = "2.0.0-pkg.1.0.0+test";
    private const string OlderVersion = "1.0.0-pkg.1.0.0+test";

    public InstanceQueryAppServiceVersionTests()
    {
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _instanceExtensionService = Substitute.For<IInstanceExtensionService>();
        _scriptContextFactory = Substitute.For<IScriptContextFactory>();
        _representationEtagService = Substitute.For<IRepresentationEtagService>();
        _schemaFieldFilterService = Substitute.For<ISchemaFieldFilterService>();

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

        _representationEtagService.Generate(Arg.Any<object>()).Returns((string?)null);

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
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: _representationEtagService,
            schemaFieldFilterService: _schemaFieldFilterService,
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task GetInstanceAsync_WithNullVersion_UsesReadOnlyPath()
    {
        var instance = CreateInstanceWithMultipleVersions();
        SetupReadOnlyMock(instance);
        SetupFlowMock(instance);

        var input = new GetInstanceInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString(),
            Version = null
        };

        var result = await _service.GetInstanceAsync(input, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierWithFullHistoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceAsync_WithSpecificVersion_UsesFullHistoryPath()
    {
        var instance = CreateInstanceWithMultipleVersions();
        SetupFullHistoryMock(instance);
        SetupFlowMock(instance);

        var input = new GetInstanceInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString(),
            Version = OlderVersion
        };

        var result = await _service.GetInstanceAsync(input, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Attributes.ShouldNotBeNull();
        await _instanceRepository.Received(1)
            .FindByIdentifierWithFullHistoryAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceAsync_WithLatestKeyword_UsesReadOnlyPath()
    {
        var instance = CreateInstanceWithMultipleVersions();
        SetupReadOnlyMock(instance);
        SetupFlowMock(instance);

        var input = new GetInstanceInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString(),
            Version = "latest"
        };

        var result = await _service.GetInstanceAsync(input, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierWithFullHistoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceDataAsync_WithSpecificVersion_UsesFullHistoryPath()
    {
        var instance = CreateInstanceWithMultipleVersions();
        SetupFullHistoryMock(instance);
        SetupFlowMock(instance);

        var input = new GetInstanceDataInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString(),
            Version = OlderVersion
        };

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        result.Result.Value!.Data.ShouldNotBeNull();
        await _instanceRepository.Received(1)
            .FindByIdentifierWithFullHistoryAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceDataAsync_WithNullVersion_UsesReadOnlyPath()
    {
        var instance = CreateInstanceWithMultipleVersions();
        SetupReadOnlyMock(instance);
        SetupFlowMock(instance);

        var input = new GetInstanceDataInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString(),
            Version = null
        };

        var result = await _service.GetInstanceDataAsync(input, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive()
            .FindByIdentifierWithFullHistoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Instance CreateInstanceWithMultipleVersions()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, TestWorkflow, LatestVersion, "test-key");
        var state = State.Create("initial", StateType.Initial, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        instance.AddDataWithVersion(
            Guid.NewGuid(),
            new JsonData("{\"key\":\"older-value\"}"),
            OlderVersion);

        instance.AddDataWithVersion(
            Guid.NewGuid(),
            new JsonData("{\"key\":\"latest-value\"}"),
            LatestVersion);

        return instance;
    }

    private void SetupReadOnlyMock(Instance instance)
    {
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);
    }

    private void SetupFullHistoryMock(Instance instance)
    {
        _instanceRepository
            .FindByIdentifierWithFullHistoryAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);
    }

    private void SetupFlowMock(Instance instance)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", instance.FlowVersion ?? LatestVersion));
        workflow.SetType("F");

        _componentCacheStore
            .GetFlowAsync(TestDomain, TestWorkflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
    }
}
