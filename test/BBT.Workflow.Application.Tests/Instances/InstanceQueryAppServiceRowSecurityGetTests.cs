using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.Application.Pagination;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Authorization.RowSecurity;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Extentions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.Logging;
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
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Instances;

/// <summary>
/// Row security on <see cref="InstanceQueryAppService.GetInstanceAsync"/> (vnext#1107, Task 7): the rule set comes
/// from <c>GetFlowAsync(domain, workflow, null)</c> (K2), D5 lookup errors go back as is, and an instance the caller
/// may not see answers exactly the missing-instance 404 — decided before extensions, ETag and 304.
/// </summary>
public sealed class InstanceQueryAppServiceRowSecurityGetTests : IDisposable
{
    private const string Domain = "core";
    private const string OlderVersion = "1.0.0";
    private const string LatestVersion = "1.0.1";

    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _repo = Substitute.For<IInstanceRepository>();
    private readonly ICallerRoleResolver _resolver = Substitute.For<ICallerRoleResolver>();
    private readonly IInstanceExtensionService _extensions = Substitute.For<IInstanceExtensionService>();
    private readonly IScriptContextFactory _scriptContextFactory = Substitute.For<IScriptContextFactory>();
    private readonly IRepresentationEtagService _etags = Substitute.For<IRepresentationEtagService>();
    private readonly IAttributeIndexCatalog _indexes = Substitute.For<IAttributeIndexCatalog>();
    private readonly ILogger<InstanceQueryAppService> _appLogger = Substitute.For<ILogger<InstanceQueryAppService>>();
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceQueryAppServiceRowSecurityGetTests()
    {
        var uow = Substitute.For<IUnitOfWorkManager>();
        uow.BeginAsync(default!, default!).ReturnsForAnyArgs(Task.FromResult(Substitute.For<IUnitOfWork>()));
        _ambientServiceProvider = new ServiceCollection().AddLogging().AddSingleton(uow).BuildServiceProvider();
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithWorkflow(Arg.Any<WorkflowDefinition?>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance>()).Returns(builder);
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithTransition(Arg.Any<string>()).Returns(builder);
        builder.WithBody(Arg.Any<JsonData>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.WithQueryParameters(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(new ScriptContext(Substitute.For<ILogger<ScriptContext>>()));
        _scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);

        _extensions.ProcessExtensionsAsync(Arg.Any<string[]?>(), Arg.Any<ScriptContext>(), Arg.Any<WorkflowDefinition>(),
                Arg.Any<ExtensionScope>(), Arg.Any<CancellationToken>())
            .Returns(Result<Dictionary<string, object>>.Ok(new Dictionary<string, object>()));
        _etags.Generate(Arg.Any<object>()).Returns("\"rep-etag\"");

        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution(["branch-staff"], CallerRoleOutcome.Resolved));
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbientServiceProvider;

    // ---- excluded ⇒ the missing-instance 404, before ETag / extensions ------------------------------------------

    [Fact]
    public async Task Get_ExcludedWithMatchingIfNoneMatch_Returns404NotNotModified()
    {
        var instance = StubInstance();
        StubSecuredFlow();
        var input = Input(instance.Id.ToString(), branch: "BR-001");
        input.IfNoneMatch = "\"rep-etag\""; // what an allowed read would have produced

        var r = await CreateService().GetInstanceAsync(input);

        r.IsNotModified.ShouldBeFalse();
        r.Result.IsSuccess.ShouldBeFalse();
        r.Result.Error!.Code.ShouldBe(WorkflowErrorCodes.NotFoundInstanceData);
        _etags.DidNotReceiveWithAnyArgs().Generate(default!);
    }

    [Fact]
    public async Task Get_FalseProbe_OverwritesOutcomeWithExcluded_AndLogs20506()
    {
        var instance = StubInstance();
        StubSecuredFlow();
        _appLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true); // LoggerMessage skips a disabled level
        // The listener is process-wide: anchor this test to its own root activity and take only spans
        // carrying its trace id, so a read span from a test running in parallel is never captured.
        using var root = new System.Diagnostics.Activity("rowsecurity-get-test-root").Start();
        var traceId = root.TraceId;
        System.Diagnostics.Activity? read = null;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == InstanceReadActivityHelper.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a =>
            {
                if (a.TraceId == traceId)
                {
                    Interlocked.CompareExchange(ref read, a, null);
                }
            }
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var r = await CreateService().GetInstanceAsync(Input(instance.Id.ToString(), branch: "BR-001"));

        r.Result.IsSuccess.ShouldBeFalse();
        read.ShouldNotBeNull();
        read!.GetTagItem("vnext.rowsecurity.outcome").ShouldBe("excluded");
        read.GetTagItem("vnext.rowsecurity.applied").ShouldBe(true);
        _appLogger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log"
            && c.GetArguments()[0] is LogLevel.Information && c.GetArguments()[1] is EventId { Id: 20506 }).ShouldBe(1);
    }

    [Fact]
    public async Task Get_Excluded_DoesNotRunExtensions()
    {
        var instance = StubInstance();
        StubSecuredFlow();
        var input = Input(instance.Id.ToString(), branch: "BR-001");
        input.Extensions = ["ext-1"];

        var r = await CreateService().GetInstanceAsync(input);

        r.Result.IsSuccess.ShouldBeFalse();
        await _extensions.DidNotReceiveWithAnyArgs().ProcessExtensionsAsync(default, default!, default!, default, default);
        _scriptContextFactory.DidNotReceiveWithAnyArgs().NewBuilder(default!);
        // The output builder's own-version flow lookup never ran either.
        await _cache.DidNotReceive().GetFlowAsync(Domain, "rs-flow", Arg.Is<string?>(v => v != null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_ByKey_ProbesResolvedInstanceId()
    {
        var instance = StubInstance("key-1");
        StubSecuredFlow();
        _repo.IsVisibleAsync(instance.Id, Arg.Any<GraphQLFilterNode>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var r = await CreateService().GetInstanceAsync(Input("key-1", branch: "BR-001"));

        r.Result.IsSuccess.ShouldBeTrue();
        r.Result.Value!.Id.ShouldBe(instance.Id);
        await _repo.Received(1).IsVisibleAsync(instance.Id,
            Arg.Is<GraphQLFilterNode>(n => Render(n).Contains("BR-001")), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_ExcludedError_EqualsMissingError()
    {
        StubInstance("key-1");
        StubSecuredFlow();
        var service = CreateService();

        var excluded = await service.GetInstanceAsync(Input("key-1", branch: "BR-001"));
        var missing = await service.GetInstanceAsync(Input("key-2", branch: "BR-001"));

        var e = excluded.Result.Error!;
        var m = missing.Result.Error!;
        e.Code.ShouldBe(m.Code);
        e.Message.ShouldBe(WorkflowErrors.InstanceNotFound("key-1").Message);
        e.Target.ShouldBe("key-1");
        m.Message.ShouldBe(WorkflowErrors.InstanceNotFound("key-2").Message);
        excluded.IsNotModified.ShouldBe(missing.IsNotModified);
    }

    [Fact]
    public async Task Get_Filtered_Visible_ReturnsBody_WithEtag()
    {
        var instance = StubInstance();
        StubSecuredFlow();
        _repo.IsVisibleAsync(instance.Id, Arg.Any<GraphQLFilterNode>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var r = await CreateService().GetInstanceAsync(Input(instance.Id.ToString(), branch: "BR-001"));

        r.Result.IsSuccess.ShouldBeTrue();
        r.Result.Value!.ETag.ShouldBe("\"rep-etag\"");
    }

    [Fact]
    public async Task Get_OldVersion_ReadsOldData_ButProbesTheInstance()
    {
        // ?version= reads an old row; visibility is decided by the probe (latest row, inside the repository).
        var instance = StubInstance(fullHistory: true);
        StubSecuredFlow();
        _repo.IsVisibleAsync(instance.Id, Arg.Any<GraphQLFilterNode>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var input = Input(instance.Id.ToString(), branch: "BR-001");
        input.Version = OlderVersion;

        var r = await CreateService().GetInstanceAsync(input);

        r.Result.IsSuccess.ShouldBeTrue();
        JsonSerializer.Serialize(r.Result.Value!.Attributes).ShouldContain("older-value");
        await _repo.Received(1).IsVisibleAsync(instance.Id, Arg.Any<GraphQLFilterNode>(), Arg.Any<SchemaFilterContext?>(),
            Arg.Any<CancellationToken>());
    }

    // ---- decisions decided without a probe ---------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_NotConfigured_NoProbe_OutputUnchanged(bool flowNotFound)
    {
        var instance = StubInstance();
        // A flow WITH a master schema but without rowSecurity: the schema must still not be loaded for the decision.
        var plain = RowSecurityServiceTests.Flow(null);
        plain.SetSchema(new Reference("rs-schema", Domain, "sys-schemas", "1.0.0"));
        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>())
            .Returns(flowNotFound
                ? Result<WorkflowDefinition>.Fail(Error.NotFound(WorkflowErrorCodes.CacheItemNotFound, "missing"))
                : Result<WorkflowDefinition>.Ok(plain));
        _cache.GetFlowAsync(Domain, "rs-flow", LatestVersion, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(null, LatestVersion)));
        var input = Input(instance.Id.ToString(), branch: "BR-001");

        var withFeature = await CreateService().GetInstanceAsync(input);
        var baselineCalls = _cache.ReceivedCalls().Count();
        _cache.ClearReceivedCalls();
        var baseline = await CreateService(withRowSecurity: false).GetInstanceAsync(input);

        withFeature.Result.IsSuccess.ShouldBeTrue();
        Serialize(withFeature.Result.Value).ShouldBe(Serialize(baseline.Result.Value));
        await _repo.DidNotReceiveWithAnyArgs().IsVisibleAsync(default, default!, default, default);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
        // C-6: the only new call is the one cached GetFlowAsync(null); no schema / index metadata.
        baselineCalls.ShouldBe(_cache.ReceivedCalls().Count() + 1);
        await _cache.DidNotReceiveWithAnyArgs().GetSchemaAsync(default!, default!, default, default);
        await _indexes.DidNotReceiveWithAnyArgs().GetReadyAsync(default!, default);
    }

    [Fact]
    public async Task Get_DeniedResolutionFailed_Is404_NoProbe()
    {
        var instance = StubInstance();
        StubSecuredFlow();
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution([], CallerRoleOutcome.Failed, "status"));

        var r = await CreateService().GetInstanceAsync(Input(instance.Id.ToString(), branch: "BR-001"));

        r.Result.Error!.ShouldBe(WorkflowErrors.InstanceNotFound(instance.Id.ToString()));
        await _repo.DidNotReceiveWithAnyArgs().IsVisibleAsync(default, default!, default, default);
        await _extensions.DidNotReceiveWithAnyArgs().ProcessExtensionsAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task Get_NoMatchingRule_Is404_NoProbe()
    {
        var instance = StubInstance();
        StubSecuredFlow();
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution(["menu.view"], CallerRoleOutcome.Resolved));

        var r = await CreateService().GetInstanceAsync(Input(instance.Id.ToString(), branch: "BR-001"));

        r.Result.Error!.Code.ShouldBe(WorkflowErrorCodes.NotFoundInstanceData);
        await _repo.DidNotReceiveWithAnyArgs().IsVisibleAsync(default, default!, default, default);
    }

    [Fact]
    public async Task Get_Unrestricted_NoProbe_ReturnsBody()
    {
        var instance = StubInstance();
        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow("""{"roles":["branch-staff"]}""")));

        var r = await CreateService().GetInstanceAsync(Input(instance.Id.ToString()));

        r.Result.IsSuccess.ShouldBeTrue();
        await _repo.DidNotReceiveWithAnyArgs().IsVisibleAsync(default, default!, default, default);
    }

    [Theory]
    [InlineData(WorkflowErrorCodes.FlowDefaultVersionInvalid)]
    [InlineData(WorkflowErrorCodes.VersionRegistryUnavailable)]
    public async Task Get_PointerInvalid_Returns100052_AsIs_ForFlowWithoutRowSecurity(string code) // D5
    {
        var instance = StubInstance();
        var err = code == WorkflowErrorCodes.FlowDefaultVersionInvalid
            ? Error.Conflict(code, "pointer invalid")
            : Error.Transient(code, "registry down");
        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>()).Returns(Result<WorkflowDefinition>.Fail(err));
        // The instance's own version has no rowSecurity — the denial does not depend on it.
        _cache.GetFlowAsync(Domain, "rs-flow", LatestVersion, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(null, LatestVersion)));

        var r = await CreateService().GetInstanceAsync(Input(instance.Id.ToString()));

        r.Result.Error.ShouldBe(err);
        r.IsNotModified.ShouldBeFalse();
        await _repo.DidNotReceiveWithAnyArgs().IsVisibleAsync(default, default!, default, default);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
        await _extensions.DidNotReceiveWithAnyArgs().ProcessExtensionsAsync(default, default!, default!, default, default);
    }

    /// <summary>
    /// A rule-set-unavailable decision that carries no lookup error, over a flow lookup that succeeded, has no
    /// error to return as is. It must still deny (fail closed with the missing-instance 404), never serve the body.
    /// </summary>
    [Fact]
    public async Task Get_DeniedRuleSetUnavailable_WithoutAnyError_FailsClosedAs404()
    {
        var instance = StubInstance();
        StubSecuredFlow();
        var service = Substitute.For<IRowSecurityService>();
        service.DecideAsync(Arg.Any<RowSecurityRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RowSecurityDecision(RowSecurityOutcome.DeniedRuleSetUnavailable, null, [], [], [], null));

        var r = await CreateService(rowSecurityService: service).GetInstanceAsync(Input(instance.Id.ToString()));

        r.Result.IsSuccess.ShouldBeFalse();
        r.Result.Error!.ShouldBe(WorkflowErrors.InstanceNotFound(instance.Id.ToString()));
        await _repo.DidNotReceiveWithAnyArgs().IsVisibleAsync(default, default!, default, default);
        await _extensions.DidNotReceiveWithAnyArgs().ProcessExtensionsAsync(default, default!, default!, default, default);
    }

    // ---- K2 ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_UsesPolicyVersion_NotInstanceFlowVersion() // K2
    {
        var instance = StubInstance(workflow: "rs-versioned"); // instance.FlowVersion = 1.0.1
        instance.FlowVersion.ShouldBe(LatestVersion);
        _cache.GetFlowAsync(Domain, "rs-versioned", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.BranchRule, OlderVersion)));
        _cache.GetFlowAsync(Domain, "rs-versioned", LatestVersion, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.RegionRule, LatestVersion)));
        var input = Input(instance.Id.ToString(), branch: "BR-001", workflow: "rs-versioned");
        input.Headers!["x-region"] = "north";

        await CreateService().GetInstanceAsync(input); // probe answers false (excluded) ⇒ no body build

        await _repo.Received(1).IsVisibleAsync(instance.Id,
            Arg.Is<GraphQLFilterNode>(n => Render(n).Contains("branchCode") && !Render(n).Contains("region")),
            Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().GetFlowAsync(Domain, "rs-versioned", LatestVersion, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAndGet_AgreeForInstancesOfBothVersions_UnderPointer()
    {
        // Instances on 1.0.0 and 1.0.1, the pointer selects 1.0.0 (branch rule). The list and both GETs hand the
        // engine the same predicate and schema context; the engine (stubbed here, proven equal on Postgres by
        // ListGetParity) then answers the same rows.
        var onOld = StubInstance(flowVersion: OlderVersion, key: "old");
        var onNew = StubInstance(flowVersion: LatestVersion, key: "new");
        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.BranchRule, OlderVersion)));
        _cache.GetFlowAsync(Domain, "rs-flow", LatestVersion, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.RegionRule, LatestVersion)));
        var visible = new HashSet<Guid> { onNew.Id };
        _repo.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<GraphQLFilterNode>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns(call => visible.Contains(call.ArgAt<Guid>(0)));
        GraphQLFilterNode? listPredicate = null;
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(),
                Arg.Do<GraphQLFilterNode>(n => listPredicate = n), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult((new HateoasPagedList<Instance>([onNew], 1, 10, false), (List<GroupSummary>?)null)));

        var service = CreateService();
        var list = await service.GetInstanceListAsync(new GetInstanceListInput
        {
            Domain = Domain, Workflow = "rs-flow", Page = 1, PageSize = 10,
            Headers = new Dictionary<string, string?> { ["x-branch-code"] = "BR-001" },
            QueryParameters = new Dictionary<string, string?>()
        });
        var getOld = await service.GetInstanceAsync(Input("old", branch: "BR-001"));
        var getNew = await service.GetInstanceAsync(Input("new", branch: "BR-001"));

        list.IsSuccess.ShouldBeTrue();
        var listIds = list.Value!.Items.Cast<GetInstanceOutput>().Select(i => i.Id!.Value).ToHashSet();
        var getIds = new[] { (onOld.Id, getOld), (onNew.Id, getNew) }.Where(x => x.Item2.Result.IsSuccess).Select(x => x.Item1);
        getIds.ToHashSet().SetEquals(listIds).ShouldBeTrue();
        listPredicate.ShouldNotBeNull();
        foreach (var id in new[] { onOld.Id, onNew.Id })
            await _repo.Received(1).IsVisibleAsync(id, Arg.Is<GraphQLFilterNode>(n => Render(n) == Render(listPredicate!)),
                Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>());
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private void StubSecuredFlow() =>
        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.BranchRule)));

    private Instance StubInstance(string? key = null, bool fullHistory = false, string workflow = "rs-flow",
        string flowVersion = LatestVersion)
    {
        var instance = Instance.Create(Guid.NewGuid(), workflow, flowVersion, key ?? "k-" + Guid.NewGuid().ToString("N"));
        instance.ChangeState(State.Create("review", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreaseMinor.Code));
        instance.SeedDataWithVersion(Guid.NewGuid(), new JsonData("{\"key\":\"older-value\"}"), OlderVersion);
        instance.SeedDataWithVersion(Guid.NewGuid(), new JsonData("{\"key\":\"latest-value\"}"), LatestVersion);
        foreach (var identifier in new[] { instance.Id.ToString(), instance.Key! })
        {
            if (fullHistory)
                _repo.FindByIdentifierWithFullHistoryAsync(identifier, Arg.Any<CancellationToken>()).Returns(instance);
            else
                _repo.FindByIdentifierAsReadOnlyAsync(identifier, Arg.Any<CancellationToken>()).Returns(instance);
        }
        return instance;
    }

    private static GetInstanceInput Input(string identifier, string? branch = null, string workflow = "rs-flow")
    {
        // GetInstanceInput is a sealed class (no `with`): every test builds its own (R-F10).
        var input = new GetInstanceInput
        {
            Domain = Domain,
            Workflow = workflow,
            Instance = identifier,
            Headers = new Dictionary<string, string?>(),
            QueryParameters = new Dictionary<string, string?>()
        };
        if (branch != null) input.Headers["x-branch-code"] = branch;
        return input;
    }

    private static string Render(GraphQLFilterNode node) => JsonSerializer.Serialize(node);

    private static string Serialize(object? value) => JsonSerializer.Serialize(value);

    private InstanceQueryAppService CreateService(bool withRowSecurity = true, IRowSecurityService? rowSecurityService = null)
    {
        var urlTemplateBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlTemplateBuilder.BuildInstanceListUrl(Arg.Any<string>(), Arg.Any<string>()).Returns("/route");
        var links = Substitute.For<IPaginationLinkGenerator>();
        links.Relative().Returns(links);
        links.GenerateLinks(Arg.Any<HateoasPagedList<GetInstanceOutput>>(), Arg.Any<string>())
            .Returns(_ => Substitute.For<PaginationLinks>());

        var currentUser = Substitute.For<ICurrentUser>();
        return new InstanceQueryAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: _runtimeInfoProvider,
            componentCacheStore: _cache,
            instanceRepository: _repo,
            instanceTransitionRepository: Substitute.For<IInstanceTransitionRepository>(),
            instanceCorrelationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            instanceIncidentRepository: Substitute.For<IInstanceIncidentRepository>(),
            instanceTaskRepository: Substitute.For<IInstanceTaskRepository>(),
            instanceActionRepository: Substitute.For<IInstanceActionRepository>(),
            longPollInteractionGate: Substitute.For<BBT.Workflow.Execution.LongPoll.ILongPollInteractionGate>(),
            instanceExtensionService: _extensions,
            scriptContextFactory: _scriptContextFactory,
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: urlTemplateBuilder,
            instanceCorrelationResolver: Substitute.For<BBT.Workflow.Instances.Correlation.IInstanceCorrelationResolver>(),
            correlationOptions: Options.Create(new BBT.Workflow.Instances.Correlation.InstanceCorrelationOptions()),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: _etags,
            instanceDataReadService: new InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
            callerRoleResolver: _resolver,
            paginationLinkGenerator: links,
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(new HumanTaskFunctionOptions()),
            attributeIndexCatalog: _indexes,
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            humanTaskFunctionCache: Substitute.For<Caching.IHumanTaskFunctionCache>(),
            currentUser: currentUser,
            descentLimiter: new HumanTaskDescentLimiter(Options.Create(new HumanTaskFunctionOptions())),
            logger: _appLogger,
            rowSecurityService: rowSecurityService ?? (withRowSecurity
                ? new RowSecurityService(_resolver, currentUser, Options.Create(new RowSecurityOptions()))
                : null));
    }
}
