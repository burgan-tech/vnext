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
/// Row security on <see cref="InstanceQueryAppService.GetInstanceListAsync"/> (vnext#1107, Task 6B): only the
/// public endpoint and the GetInstances task (ApplyRowSecurity) are filtered, D5 lookup errors go back as is, no SQL for a no-rows decision (SG-1), the row predicate on both repository routes, and the
/// NotConfigured path unchanged (C-6). Every "repository not called" test runs once per route (R-F3).
/// </summary>
public sealed class InstanceQueryAppServiceRowSecurityListTests : IDisposable
{
    private const string Domain = "core";

    public const string NoFilter = "none";
    public const string Envelope = "envelope";
    public const string GroupBy = "groupBy";

    private const string EnvelopeFilter = """{"filter":{"status":{"eq":"A"}},"aggregations":{"count":true}}""";
    private const string GroupByQuery = """{"field":"status"}""";

    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _repo = Substitute.For<IInstanceRepository>();
    private readonly ICallerRoleResolver _resolver = Substitute.For<ICallerRoleResolver>();
    private readonly IPaginationLinkGenerator _links = Substitute.For<IPaginationLinkGenerator>();
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceQueryAppServiceRowSecurityListTests()
    {
        var uow = Substitute.For<IUnitOfWorkManager>();
        uow.BeginAsync(default!, default!).ReturnsForAnyArgs(Task.FromResult(Substitute.For<IUnitOfWork>()));
        _ambientServiceProvider = new ServiceCollection().AddSingleton(uow).BuildServiceProvider();
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        _links.Relative().Returns(_links);
        _links.GenerateLinks(Arg.Any<HateoasPagedList<GetInstanceOutput>>(), Arg.Any<string>())
            .Returns(_ => Substitute.For<PaginationLinks>());
        _links.GenerateLinks(Arg.Any<HateoasPagedList<GroupSummary>>(), Arg.Any<string>())
            .Returns(_ => Substitute.For<PaginationLinks>());

        var empty = (new HateoasPagedList<Instance>([], 1, 10, false), (List<GroupSummary>?)null);
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(empty));
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<GraphQLFilterNode>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(empty));
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<GraphQLFilterRequest?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(empty));

        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution(["branch-staff"], CallerRoleOutcome.Resolved));
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbientServiceProvider;

    public static TheoryData<string> Routes => new() { NoFilter, Envelope, GroupBy };

    // ---- D5 ----------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> CodesTimesRoutes()
    {
        var data = new TheoryData<string, string>();
        foreach (var code in new[] { WorkflowErrorCodes.FlowDefaultVersionInvalid, WorkflowErrorCodes.VersionRegistryUnavailable })
        foreach (var route in new[] { NoFilter, Envelope, GroupBy })
            data.Add(code, route);
        return data;
    }

    [Theory]
    [MemberData(nameof(CodesTimesRoutes))]
    public async Task List_PointerOrRegistryError_IsReturnedAsIs_EvenWithoutRowSecurity(string code, string route) // D5
    {
        var err = code == WorkflowErrorCodes.FlowDefaultVersionInvalid
            ? Error.Conflict(code, "pointer invalid")
            : Error.Transient(code, "registry down");
        _cache.GetFlowAsync(Domain, "rs-plain", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Fail(err));

        var result = await InvokeAsync(ListInput("rs-plain", route));

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(err);
        AssertRepositoryNotCalled();
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    /// <summary>An in-process reader (view resolution, triggers, GetInstanceData) never asks, so a lookup error does not fail it.</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task List_InProcessCaller_IsNotDenied_OnLookupError(string route)
    {
        _cache.GetFlowAsync(Domain, "rs-plain", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Fail(Error.Transient(WorkflowErrorCodes.VersionRegistryUnavailable, "down")));
        var input = ListInput("rs-plain", route);
        input.ApplyRowSecurity = false;

        var result = await InvokeAsync(input);

        result.IsSuccess.ShouldBeTrue();
        CallsNamed().Count().ShouldBe(1);
    }

    // ---- scope: endpoint only ----------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task List_InProcessCaller_IsNotFiltered_EvenForRowSecuredFlow(string route)
    {
        StubSecuredFlow();
        var input = ListInput("rs-flow", route); // no header: the endpoint would return no rows
        input.ApplyRowSecurity = false;

        var result = await InvokeAsync(input);

        result.IsSuccess.ShouldBeTrue();
        CallsNamed().Count().ShouldBe(1);
        CallsNamed().SelectMany(c => c.GetArguments()).OfType<GraphQLFilterNode>().ShouldBeEmpty();
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    // ---- SG-1 --------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task List_WhenDecisionIsNone_DoesNotCallRepository(string route) // SG-1
    {
        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.BranchRule)));
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution(["menu.view"], CallerRoleOutcome.Resolved)); // no match ⇒ none

        var result = await InvokeAsync(ListInput("rs-flow", route));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.ShouldBeEmpty();
        result.Value.Links.ShouldNotBeNull();
        AssertRepositoryNotCalled();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task List_ResolutionFailed_DoesNotCallRepository(string route)
    {
        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.BranchRule)));
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new CallerRoleResolution([], CallerRoleOutcome.Failed, "status"));

        var result = await InvokeAsync(ListInput("rs-flow", route, branch: "BR-001"));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.ShouldBeEmpty();
        AssertRepositoryNotCalled();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task List_NoRows_HasSameShapeAsEmptyRepositoryResult(string route)
    {
        // Baseline: a flow without rowSecurity whose repository returns nothing (empty groups when grouping).
        _cache.GetFlowAsync(Domain, "rs-plain", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(null)));
        var emptyGroups = (new HateoasPagedList<Instance>([], 1, 10, false),
            (List<GroupSummary>?)(route == NoFilter ? null : new List<GroupSummary>()));
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(emptyGroups));
        _repo.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<GraphQLFilterRequest?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(emptyGroups));
        var baseline = await InvokeAsync(ListInput("rs-plain", route));

        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.BranchRule)));
        var denied = await InvokeAsync(ListInput("rs-flow", route)); // header missing ⇒ no contribution

        Serialize(denied.Value).ShouldBe(Serialize(baseline.Value));
    }

    // ---- Filtered ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_Filtered_EnvelopeRoute_SetsRowPredicate()
    {
        StubSecuredFlow();

        var result = await InvokeAsync(ListInput("rs-flow", Envelope, branch: "BR-001"));

        result.IsSuccess.ShouldBeTrue();
        await _repo.Received(1).GetPagedResultsWithGroupsAsync(1, 10,
            Arg.Is<GraphQLFilterRequest?>(r => r != null && r.RowPredicate != null
                                               && Render(r.RowPredicate).Contains("BR-001")
                                               && r.Filter != null && !Render(r.Filter).Contains("BR-001")),
            Arg.Any<CancellationToken>());
        CallsNamed().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(NoFilter)]
    [InlineData(GroupBy)]
    public async Task List_Filtered_StringRoute_UsesNewOverload(string route)
    {
        StubSecuredFlow();
        var input = ListInput("rs-flow", route, branch: "BR-001");

        var result = await InvokeAsync(input);

        result.IsSuccess.ShouldBeTrue();
        await _repo.Received(1).GetPagedResultsWithGroupsAsync(1, 10, input.Filter, input.GroupBy, null, input.Sort,
            Arg.Any<SchemaFilterContext?>(), Arg.Is<GraphQLFilterNode>(n => Render(n).Contains("BR-001")),
            Arg.Any<CancellationToken>());
        CallsNamed().Count().ShouldBe(1);
    }

    [Fact]
    public async Task List_Unrestricted_CallsExistingOverload()
    {
        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow("""{"roles":["branch-staff"]}""")));

        var result = await InvokeAsync(ListInput("rs-flow", NoFilter));

        result.IsSuccess.ShouldBeTrue();
        await _repo.Received(1).GetPagedResultsWithGroupsAsync(1, 10, null, null, null, null,
            Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>());
        CallsNamed().Count().ShouldBe(1);
    }

    // ---- NotConfigured (C-6) -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_NotConfigured_CallsExistingOverloadWithSameArguments(bool flowNotFound)
    {
        _cache.GetFlowAsync(Domain, "rs-plain", null, Arg.Any<CancellationToken>())
            .Returns(flowNotFound
                ? Result<WorkflowDefinition>.Fail(Error.NotFound(WorkflowErrorCodes.CacheItemNotFound, "missing"))
                : Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(null)));
        const string filter = """{"attributes":{"amount":{"ge":100}}}""";
        const string sort = """{"field":"createdAt","direction":"desc"}""";
        var input = ListInput("rs-plain", NoFilter);
        input.Filter = filter;
        input.Sort = sort;

        var result = await InvokeAsync(input);

        result.IsSuccess.ShouldBeTrue();
        await _repo.Received(1).GetPagedResultsWithGroupsAsync(1, 10, filter, null, null, sort,
            null, Arg.Any<CancellationToken>());
        CallsNamed().Count().ShouldBe(1);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Fact]
    public async Task List_NotConfigured_EnvelopeRoute_LeavesRowPredicateUnset()
    {
        _cache.GetFlowAsync(Domain, "rs-plain", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(null)));

        await InvokeAsync(ListInput("rs-plain", Envelope));

        await _repo.Received(1).GetPagedResultsWithGroupsAsync(1, 10,
            Arg.Is<GraphQLFilterRequest?>(r => r != null && r.RowPredicate == null), Arg.Any<CancellationToken>());
    }

    // ---- K2 / C-4 ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_UsesPolicyVersionFromLookup() // K2: pointer selects 1.0.0 while 1.0.1 exists
    {
        _cache.GetFlowAsync(Domain, "rs-versioned", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.BranchRule, "1.0.0")));
        _cache.GetFlowAsync(Domain, "rs-versioned", "1.0.1", Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.RegionRule, "1.0.1")));

        var input = ListInput("rs-versioned", NoFilter, branch: "BR-001");
        input.Headers!["x-region"] = "north";
        var result = await InvokeAsync(input);

        result.IsSuccess.ShouldBeTrue();
        await _repo.Received(1).GetPagedResultsWithGroupsAsync(1, 10, null, null, null, null,
            Arg.Any<SchemaFilterContext?>(),
            Arg.Is<GraphQLFilterNode>(n => Render(n).Contains("branchCode") && !Render(n).Contains("region")),
            Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string?>(v => v != null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_RuleNotCountedAgainstCallerFilterLimit() // C-4
    {
        StubSecuredFlow();
        var padding = new string('x', 995 - """{"attributes":{"note":{"eq":""}}}""".Length);
        var filter = "{\"attributes\":{\"note\":{\"eq\":\"" + padding + "\"}}}";
        filter.Length.ShouldBe(995);
        var input = ListInput("rs-flow", NoFilter, branch: "BR-001");
        input.Filter = filter;

        var result = await InvokeAsync(input);

        result.IsSuccess.ShouldBeTrue();
        // The caller's filter reaches the repository verbatim; the rule travels separately.
        await _repo.Received(1).GetPagedResultsWithGroupsAsync(1, 10, filter, null, null, null,
            Arg.Any<SchemaFilterContext?>(), Arg.Any<GraphQLFilterNode>(), Arg.Any<CancellationToken>());
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private void StubSecuredFlow() =>
        _cache.GetFlowAsync(Domain, "rs-flow", null, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(RowSecurityServiceTests.Flow(RowSecurityServiceTests.BranchRule)));

    private static GetInstanceListInput ListInput(string workflow, string route, string? branch = null)
    {
        var input = new GetInstanceListInput
        {
            Domain = Domain,
            Workflow = workflow,
            Page = 1,
            PageSize = 10,
            Filter = route == Envelope ? EnvelopeFilter : null,
            Headers = new Dictionary<string, string?>(),
            QueryParameters = new Dictionary<string, string?>(),
            ApplyRowSecurity = true
        };
        if (route == GroupBy) input.QueryParameters["groupBy"] = GroupByQuery;
        if (branch != null) input.Headers["x-branch-code"] = branch;
        return input;
    }

    private IEnumerable<NSubstitute.Core.ICall> CallsNamed() =>
        _repo.ReceivedCalls().Where(c => c.GetMethodInfo().Name.StartsWith("GetPaged", StringComparison.Ordinal));

    private void AssertRepositoryNotCalled() => CallsNamed().ShouldBeEmpty(); // R-F3: every GetPaged* member

    private static string Render(GraphQLFilterNode node) => JsonSerializer.Serialize(node);

    private static string Serialize(object? value) => JsonSerializer.Serialize(value);

    private Task<Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> InvokeAsync(GetInstanceListInput input) =>
        CreateService().GetInstanceListAsync(input, CancellationToken.None);

    private InstanceQueryAppService CreateService()
    {
        var urlTemplateBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlTemplateBuilder.BuildInstanceListUrl(Arg.Any<string>(), Arg.Any<string>()).Returns("/route");

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
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: urlTemplateBuilder,
            instanceCorrelationResolver: Substitute.For<BBT.Workflow.Instances.Correlation.IInstanceCorrelationResolver>(),
            correlationOptions: Options.Create(new BBT.Workflow.Instances.Correlation.InstanceCorrelationOptions()),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
            callerRoleResolver: _resolver,
            paginationLinkGenerator: _links,
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(new HumanTaskFunctionOptions()),
            attributeIndexCatalog: Substitute.For<IAttributeIndexCatalog>(),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            humanTaskFunctionCache: Substitute.For<Caching.IHumanTaskFunctionCache>(),
            currentUser: currentUser,
            descentLimiter: new HumanTaskDescentLimiter(Options.Create(new HumanTaskFunctionOptions())),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>(),
            rowSecurityService: new RowSecurityService(_resolver, currentUser, Options.Create(new RowSecurityOptions())));
    }
}
