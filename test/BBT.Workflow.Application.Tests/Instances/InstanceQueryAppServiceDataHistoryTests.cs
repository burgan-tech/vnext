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
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Extentions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The data-history reads of <see cref="InstanceQueryAppService"/>: the paged list of an instance's data rows and the
/// single-row read. Every row served goes through the same exposure pass as the data function (a real
/// <see cref="InstanceDataReadService"/> over a substituted <see cref="ISchemaFieldFilterService"/>), never as stored.
/// </summary>
public class InstanceQueryAppServiceDataHistoryTests : IDisposable
{
    private readonly IComponentCacheStore _componentCacheStore;
    private readonly IInstanceRepository _instanceRepository;
    private readonly ISchemaFieldFilterService _schemaFieldFilterService;
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";

    public InstanceQueryAppServiceDataHistoryTests()
    {
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
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
        _ambientServiceProvider = services.BuildServiceProvider();

        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        _service = new InstanceQueryAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceTransitionRepository: Substitute.For<IInstanceTransitionRepository>(),
            instanceCorrelationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            instanceIncidentRepository: Substitute.For<IInstanceIncidentRepository>(),
            instanceTaskRepository: Substitute.For<IInstanceTaskRepository>(),
            instanceActionRepository: Substitute.For<IInstanceActionRepository>(),
            longPollInteractionGate: Substitute.For<BBT.Workflow.Execution.LongPoll.ILongPollInteractionGate>(),
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: Substitute.For<IUrlTemplateBuilder>(),
            instanceCorrelationResolver: Substitute.For<BBT.Workflow.Instances.Correlation.IInstanceCorrelationResolver>(),
            correlationOptions: Options.Create(new BBT.Workflow.Instances.Correlation.InstanceCorrelationOptions()),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new InstanceDataReadService(_schemaFieldFilterService),
            callerRoleResolver: new DefaultCallerRoleResolver(Substitute.For<ICurrentUser>()),
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(new HumanTaskFunctionOptions()),
            attributeIndexCatalog: Substitute.For<IAttributeIndexCatalog>(),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            humanTaskFunctionCache: Substitute.For<Caching.IHumanTaskFunctionCache>(),
            currentUser: Substitute.For<ICurrentUser>(),
            descentLimiter: new HumanTaskDescentLimiter(Options.Create(new HumanTaskFunctionOptions())),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    // ── Paged history ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetInstanceDataHistoryAsync_ReturnsTheRepositoryPageInOrderWithItsPaging()
    {
        var instance = CreateInstanceWithRows(out var rows);
        SetupInstanceAndFlow(instance);
        var newestFirst = rows.OrderByDescending(r => r.Version).ToList();
        _instanceRepository
            .GetDataHistoryPagedAsync(instance.Id, 2, 3, Arg.Any<CancellationToken>())
            .Returns(new HateoasPagedList<InstanceData>(newestFirst, 2, 3, true));
        SetupFilterEcho();

        var result = await _service.GetInstanceDataHistoryAsync(
            CreateInput(instance.Id.ToString(), page: 2, pageSize: 3), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var output = result.Value!;
        output.Items.Select(i => i.Id).ShouldBe(newestFirst.Select(r => r.Id));
        output.Items.Select(i => i.Version).ShouldBe(["1.2.0", "1.1.0", "1.0.0"]);
        output.Items[0].IsLatest.ShouldBeTrue();
        output.Items[0].ETag.ShouldBe(newestFirst[0].ETag);
        output.Items[0].VersionNo.ShouldBe(newestFirst[0].VersionNo);
        output.Items[0].EnteredAt.ShouldBe(newestFirst[0].EnteredAt);
        output.Page.ShouldBe(2);
        output.PageSize.ShouldBe(3);
        output.HasNext.ShouldBeTrue();
    }

    [Fact]
    public async Task GetInstanceDataHistoryAsync_WhenIncludeData_ExposesEveryRowThroughTheFieldFilter()
    {
        var instance = CreateInstanceWithRows(out var rows);
        SetupInstanceAndFlow(instance);
        _instanceRepository
            .GetDataHistoryPagedAsync(instance.Id, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new HateoasPagedList<InstanceData>(rows.ToList(), 1, 20, false));
        _schemaFieldFilterService
            .ApplyAsync(Arg.Any<Definitions.Workflow?>(), Arg.Any<JsonElement?>(), Arg.Any<Instance>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(call =>
            {
                var raw = call.ArgAt<JsonElement?>(1)!.Value.GetProperty("step").GetString();
                return Task.FromResult<JsonElement?>(Json($$"""{ "exposed": "{{raw}}" }"""));
            });

        var result = await _service.GetInstanceDataHistoryAsync(
            CreateInput(instance.Id.ToString()), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _schemaFieldFilterService.Received(rows.Count)
            .ApplyAsync(Arg.Any<Definitions.Workflow?>(), Arg.Any<JsonElement?>(), Arg.Any<Instance>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>());
        foreach (var item in result.Value!.Items)
        {
            item.Data.ShouldNotBeNull();
            item.Data!.Value.TryGetProperty("step", out _).ShouldBeFalse("the raw row must never be served");
            item.Data!.Value.GetProperty("exposed").GetString().ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GetInstanceDataHistoryAsync_WhenIncludeDataIsFalse_ServesNoDataAndSkipsTheFilter()
    {
        var instance = CreateInstanceWithRows(out var rows);
        SetupInstanceAndFlow(instance);
        _instanceRepository
            .GetDataHistoryPagedAsync(instance.Id, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new HateoasPagedList<InstanceData>(rows.ToList(), 1, 20, false));

        var input = CreateInput(instance.Id.ToString());
        input.IncludeData = false;

        var result = await _service.GetInstanceDataHistoryAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.Count.ShouldBe(rows.Count);
        result.Value.Items.ShouldAllBe(i => i.Data == null);
        await _schemaFieldFilterService.DidNotReceiveWithAnyArgs()
            .ApplyAsync(default, default, default!, default, default, default);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, 1000, 1, GetInstanceDataHistoryInput.MaxPageSize)]
    [InlineData(3, 50, 3, 50)]
    public async Task GetInstanceDataHistoryAsync_ClampsPageAndPageSize(
        int page, int pageSize, int expectedPage, int expectedPageSize)
    {
        var instance = CreateInstanceWithRows(out _);
        SetupInstanceAndFlow(instance);
        _instanceRepository
            .GetDataHistoryPagedAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new HateoasPagedList<InstanceData>([], expectedPage, expectedPageSize, false));

        var result = await _service.GetInstanceDataHistoryAsync(
            CreateInput(instance.Id.ToString(), page, pageSize), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.Received(1)
            .GetDataHistoryPagedAsync(instance.Id, expectedPage, expectedPageSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceDataHistoryAsync_WhenInstanceIsUnknown_ReturnsInstanceNotFoundWithoutReadingRows()
    {
        var unknown = Guid.NewGuid().ToString();
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(unknown, Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await _service.GetInstanceDataHistoryAsync(CreateInput(unknown), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.NotFoundInstanceData);
        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .GetDataHistoryPagedAsync(default, default, default, default);
    }

    // ── Single row ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetInstanceDataHistoryRowAsync_WhenRowBelongsToTheInstance_ServesTheExposedRow()
    {
        var instance = CreateInstanceWithRows(out var rows);
        SetupInstanceAndFlow(instance);
        var row = rows[1];
        _instanceRepository.FindDataRowAsync(instance.Id, row.Id, Arg.Any<CancellationToken>()).Returns(row);
        _schemaFieldFilterService
            .ApplyAsync(Arg.Any<Definitions.Workflow?>(), Arg.Any<JsonElement?>(), Arg.Any<Instance>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(Task.FromResult<JsonElement?>(Json("""{ "masked": "***" }""")));

        var result = await _service.GetInstanceDataHistoryRowAsync(
            CreateRowInput(instance.Id.ToString(), row.Id), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var item = result.Value!;
        item.Id.ShouldBe(row.Id);
        item.Version.ShouldBe(row.Version);
        item.ETag.ShouldBe(row.ETag);
        item.Data!.Value.GetProperty("masked").GetString().ShouldBe("***");
        await _schemaFieldFilterService.Received(1)
            .ApplyAsync(Arg.Any<Definitions.Workflow?>(), Arg.Any<JsonElement?>(), Arg.Any<Instance>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Fact]
    public async Task GetInstanceDataHistoryRowAsync_WhenRowIsUnknown_ReturnsInstanceDataNotFound()
    {
        var instance = CreateInstanceWithRows(out _);
        SetupInstanceAndFlow(instance);
        var rowId = Guid.NewGuid();
        _instanceRepository
            .FindDataRowAsync(instance.Id, rowId, Arg.Any<CancellationToken>())
            .Returns((InstanceData?)null);

        var result = await _service.GetInstanceDataHistoryRowAsync(
            CreateRowInput(instance.Id.ToString(), rowId), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.NotFoundInstanceData);
        await _instanceRepository.Received(1).FindDataRowAsync(instance.Id, rowId, Arg.Any<CancellationToken>());
        await _schemaFieldFilterService.DidNotReceiveWithAnyArgs()
            .ApplyAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task GetInstanceDataHistoryRowAsync_WhenRepositoryHandsBackAForeignRow_StillReturnsNotFound()
    {
        var instance = CreateInstanceWithRows(out _);
        var foreign = CreateInstanceWithRows(out var foreignRows);
        SetupInstanceAndFlow(instance);
        var foreignRow = foreignRows[0];
        _instanceRepository
            .FindDataRowAsync(Arg.Any<Guid>(), foreignRow.Id, Arg.Any<CancellationToken>())
            .Returns(foreignRow);

        var result = await _service.GetInstanceDataHistoryRowAsync(
            CreateRowInput(instance.Id.ToString(), foreignRow.Id), CancellationToken.None);

        foreign.Id.ShouldNotBe(instance.Id);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.NotFoundInstanceData);
        await _schemaFieldFilterService.DidNotReceiveWithAnyArgs()
            .ApplyAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task GetInstanceDataHistoryRowAsync_WhenInstanceIsUnknown_ReturnsInstanceNotFoundWithoutReadingTheRow()
    {
        var unknown = Guid.NewGuid().ToString();
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(unknown, Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await _service.GetInstanceDataHistoryRowAsync(
            CreateRowInput(unknown, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.NotFoundInstanceData);
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindDataRowAsync(default, default, default);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static GetInstanceDataHistoryInput CreateInput(string instance, int page = 1, int pageSize = 20) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instance,
        Page = page,
        PageSize = pageSize,
        Headers = new Dictionary<string, string?>(),
        QueryParameters = new Dictionary<string, string?>()
    };

    private static GetInstanceDataHistoryRowInput CreateRowInput(string instance, Guid rowId) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instance,
        RowId = rowId,
        Headers = new Dictionary<string, string?>(),
        QueryParameters = new Dictionary<string, string?>()
    };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private void SetupFilterEcho() =>
        _schemaFieldFilterService
            .ApplyAsync(Arg.Any<Definitions.Workflow?>(), Arg.Any<JsonElement?>(), Arg.Any<Instance>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(call => Task.FromResult(call.ArgAt<JsonElement?>(1)));

    /// <summary>An instance with three data rows (1.0.0, 1.1.0, 1.2.0 — the last is latest).</summary>
    private static Instance CreateInstanceWithRows(out IReadOnlyList<InstanceData> rows)
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create("review", StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);
        rows =
        [
            instance.SeedDataWithVersion(Guid.NewGuid(), new JsonData("{\"step\":\"one\"}"), "1.0.0"),
            instance.SeedDataWithVersion(Guid.NewGuid(), new JsonData("{\"step\":\"two\"}"), "1.1.0"),
            instance.SeedDataWithVersion(Guid.NewGuid(), new JsonData("{\"step\":\"three\"}"), "1.2.0")
        ];
        return instance;
    }

    private void SetupInstanceAndFlow(Instance instance)
    {
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);

        const string json = """
                            {
                                "type": "F",
                                "labels": [], "functions": [], "features": [], "states": [],
                                "sharedTransitions": [], "extensions": [],
                                "startTransition": {"key": "start", "from": null, "target": "review", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                            }
                            """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));

        _componentCacheStore
            .GetFlowAsync(TestDomain, TestWorkflow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
    }
}
