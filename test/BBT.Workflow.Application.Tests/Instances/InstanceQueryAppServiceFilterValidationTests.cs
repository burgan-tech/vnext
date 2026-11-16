using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Application.Pagination;
using BBT.Aether.MultiSchema;
using BBT.Aether.Users;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Extentions;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Gateway;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

using BBT.Workflow.Instances.HumanTask;

namespace BBT.Workflow.Instances;

/// <summary>
/// Verifies that <see cref="InstanceQueryAppService.GetInstanceListAsync"/> rejects a query it
/// cannot execute as authored, <em>before</em> touching the repository.
/// </summary>
/// <remarks>
/// The central assertion in each rejection case is
/// <c>_instanceRepository.DidNotReceiveWithAnyArgs()</c>. Previously an unsupported operator or a
/// malformed filter reached the repository, was swallowed there, and returned every row with
/// HTTP 200 — so "the query never ran" is the property that actually closes the hole.
/// </remarks>
public sealed class InstanceQueryAppServiceFilterValidationTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "wf-key";

    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IPaginationLinkGenerator _paginationLinkGenerator = Substitute.For<IPaginationLinkGenerator>();
    private readonly IInstanceExtensionService _instanceExtensionService = Substitute.For<IInstanceExtensionService>();
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceQueryAppServiceFilterValidationTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(default!, default!)
            .ReturnsForAnyArgs(Task.FromResult(Substitute.For<IUnitOfWork>()));

        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        _ambientServiceProvider = services.BuildServiceProvider();

        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        _paginationLinkGenerator.Relative().Returns(_paginationLinkGenerator);
        _paginationLinkGenerator
            .GenerateLinks(Arg.Any<HateoasPagedList<GetInstanceOutput>>(), Arg.Any<string>())
            .Returns(_ => Substitute.For<PaginationLinks>());

        // No flow/schema is stubbed, so schema resolution fails. Validation must not depend on it.
        _instanceRepository
            .GetPagedResultsWithGroupsAsync(
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                (new HateoasPagedList<Instance>([], 1, 10, false), (List<GroupSummary>?)null)));
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbientServiceProvider;

    [Theory]
    // Unsupported operator using the schema-side spelling.
    [InlineData("""{"attributes":{"amount":{"gte":100}}}""", null)]
    // Entirely unknown operator.
    [InlineData("""{"attributes":{"amount":{"zzz":100}}}""", null)]
    // Truncated so it no longer ends with a brace: never parsed, no filter applied.
    [InlineData("""{"attributes":{"amount":{"eq":100""", null)]
    // One brace short.
    [InlineData("""{"attributes":{"amount":{"eq":100}}""", null)]
    // Field with no operator at all.
    [InlineData("""{"attributes":{"amount":{}}}""", null)]
    public async Task GetInstanceListAsync_ShouldRejectBadFilter_WithoutQuerying(string filter, string? sort)
    {
        var result = await InvokeAsync(filter, sort);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceFilterInvalid);

        await _instanceRepository.DidNotReceiveWithAnyArgs().GetPagedResultsWithGroupsAsync(
            default, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task GetInstanceListAsync_ShouldSuggestTheCorrectOperator()
    {
        var result = await InvokeAsync("""{"attributes":{"amount":{"gte":100}}}""");

        result.Error.Message.ShouldContain("Did you mean 'ge'?");
    }

    [Fact]
    public async Task GetInstanceListAsync_ShouldReportEveryReason()
    {
        var result = await InvokeAsync(
            filter: """{"attributes":{"amount":{"gte":1},"score":{"lte":2}}}""",
            sort: """{"field":"nope"}""");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ValidationErrors.ShouldNotBeNull();
        result.Error.ValidationErrors!.Count.ShouldBe(3);
        result.Error.ValidationErrors.ShouldContain(v => v.MemberNames.Contains("sort.field"));
    }

    [Theory]
    [InlineData("""{"field":"nope"}""")]
    [InlineData("""{"field":"createdAt","direction":"sideways"}""")]
    [InlineData("not valid json")]
    public async Task GetInstanceListAsync_ShouldRejectBadSort_WithoutQuerying(string sort)
    {
        var result = await InvokeAsync(filter: null, sort: sort);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceSortInvalid);

        await _instanceRepository.DidNotReceiveWithAnyArgs().GetPagedResultsWithGroupsAsync(
            default, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task GetInstanceListAsync_ShouldRejectBadAggregations_WithoutQuerying()
    {
        var result = await InvokeAsync(aggregations: """{"median":"attributes.amount"}""");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceAggregationInvalid);

        await _instanceRepository.DidNotReceiveWithAnyArgs().GetPagedResultsWithGroupsAsync(
            default, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task GetInstanceListAsync_ShouldPassValidFilterThroughUnchanged()
    {
        const string filter = """{"attributes":{"amount":{"ge":100}}}""";
        const string sort = """{"field":"createdAt","direction":"desc"}""";

        var result = await InvokeAsync(filter, sort);

        result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.Received(1).GetPagedResultsWithGroupsAsync(
            1, 10, filter, null, null, sort,
            Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("eq", "not-an-id", """{"id":{"eq":"not-an-id"}}""")]
    [InlineData("in", "kimlik-degil", """{"id":{"in":["kimlik-degil"]}}""")]
    [InlineData("gt", "00000000-0000-0000-0000-000000000001", """{"id":{"gt":"00000000-0000-0000-0000-000000000001"}}""")]
    public async Task GetInstanceListAsync_ShouldMapIdFilterCompileFailure_ToFilterInvalid(
        string op, string value, string filter)
    {
        // The boundary validator checks a filter's shape, not its values, so a non-Guid `id` reaches
        // the SQL builder. Let the repository surface exactly what the builder throws there, and pin
        // that the caller gets the 400 code (InstanceFilterInvalid), not a 500 (#1080).
        _instanceRepository
            .GetPagedResultsWithGroupsAsync(
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<(HateoasPagedList<Instance>, List<GroupSummary>?)>>(_ =>
            {
                var parameterIndex = 0;
                InstanceColumnConditionBuilder.BuildCondition("id", op, value, ref parameterIndex);
                throw new InvalidOperationException("The builder was expected to reject the id filter.");
            });

        var result = await InvokeAsync(filter);

        // A Validation-prefixed error, not a thrown one re-prefixed as Failure: the prefix is what every
        // Result reader classifies by, and the GetInstances task read Failure as a 500 (#1080).
        result.IsSuccess.ShouldBeFalse();
        result.Error.Prefix.ShouldBe(BBT.Aether.Results.ErrorCodes.Prefixes.Validation);
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceFilterInvalid);
        result.Error.Message.ShouldStartWith("Filter could not be applied:");

        // The filter passed the boundary validator, so the repository was really asked to run it.
        await _instanceRepository.ReceivedWithAnyArgs(1).GetPagedResultsWithGroupsAsync(
            default, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task GetInstanceListAsync_ShouldKeepExtensionErrorCodeAndMessage_WhenExtensionFails()
    {
        // One row on the page, a resolvable flow, and an extension that fails. The list is
        // fail-fast, so the extension's error is the answer — with its code still in the code slot
        // (that code's prefix picks the HTTP status) and its text still in the message slot.
        var instance = Instance.Create(Guid.NewGuid(), WorkflowKey, "1.0.0", "row-1");
        _instanceRepository
            .GetPagedResultsWithGroupsAsync(
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<SchemaFilterContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                (new HateoasPagedList<Instance>([instance], 1, 10, false), (List<GroupSummary>?)null)));

        _componentCacheStore
            .GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BBT.Aether.Results.Result<Definitions.Workflow>.Ok(
                System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(
                    """{"type":"F","states":[],"extensions":[]}""",
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!));

        const string extensionCode = "Validation:777001";
        const string extensionMessage = "Extension 'credit-score' rejected the request.";
        _instanceExtensionService
            .ProcessExtensionsAsync(
                Arg.Any<string[]?>(), Arg.Any<ScriptContext>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<ExtensionScope>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(BBT.Aether.Results.Result<Dictionary<string, object>>.Fail(
                BBT.Aether.Results.Error.Validation(extensionCode, extensionMessage)));

        var result = await InvokeAsync();

        // Returned as is: the prefix too, since it is what picks the HTTP status (400 here, not 500).
        result.IsSuccess.ShouldBeFalse();
        result.Error.Prefix.ShouldBe(BBT.Aether.Results.ErrorCodes.Prefixes.Validation);
        result.Error.Code.ShouldBe(extensionCode);
        result.Error.Message.ShouldBe(extensionMessage);
    }

    [Fact]
    public async Task GetInstanceListAsync_ShouldQuery_WhenNoFilterSupplied()
    {
        var result = await InvokeAsync();

        result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.ReceivedWithAnyArgs(1).GetPagedResultsWithGroupsAsync(
            default, default, default, default, default, default, default, default);
    }

    private Task<BBT.Aether.Results.Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> InvokeAsync(
        string? filter = null,
        string? sort = null,
        string? groupBy = null,
        string? aggregations = null)
    {
        var urlTemplateBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlTemplateBuilder.BuildInstanceListUrl(Domain, WorkflowKey).Returns("/route");

        // GroupBy and Aggregations are derived from the raw query bag, not settable properties.
        var queryParameters = new Dictionary<string, string?>();
        if (groupBy != null) queryParameters["groupBy"] = groupBy;
        if (aggregations != null) queryParameters["aggregations"] = aggregations;

        var input = new GetInstanceListInput
        {
            Domain = Domain,
            Workflow = WorkflowKey,
            Page = 1,
            PageSize = 10,
            Filter = filter,
            Sort = sort,
            QueryParameters = queryParameters
        };

        return CreateService(urlTemplateBuilder).GetInstanceListAsync(input, CancellationToken.None);
    }

    private InstanceQueryAppService CreateService(IUrlTemplateBuilder urlTemplateBuilder)
    {
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

        var scriptContextFactory = Substitute.For<IScriptContextFactory>();
        scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(scriptContextBuilder);

        return new InstanceQueryAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: _runtimeInfoProvider,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceTransitionRepository: Substitute.For<IInstanceTransitionRepository>(),
            instanceCorrelationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            instanceIncidentRepository: Substitute.For<IInstanceIncidentRepository>(),
            instanceTaskRepository: Substitute.For<IInstanceTaskRepository>(),
            instanceActionRepository: Substitute.For<IInstanceActionRepository>(),
            longPollInteractionGate: NSubstitute.Substitute.For<BBT.Workflow.Execution.LongPoll.ILongPollInteractionGate>(),
            instanceExtensionService: _instanceExtensionService,
            scriptContextFactory: scriptContextFactory,
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: urlTemplateBuilder,
            instanceCorrelationResolver: Substitute.For<BBT.Workflow.Instances.Correlation.IInstanceCorrelationResolver>(),
            correlationOptions: Microsoft.Extensions.Options.Options.Create(new BBT.Workflow.Instances.Correlation.InstanceCorrelationOptions()),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new BBT.Workflow.Instances.InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
            callerRoleResolver: Substitute.For<ICallerRoleResolver>(),
            paginationLinkGenerator: _paginationLinkGenerator,
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(new HumanTaskFunctionOptions()),
            attributeIndexCatalog: Substitute.For<IAttributeIndexCatalog>(),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),

            humanTaskFunctionCache: Substitute.For<Caching.IHumanTaskFunctionCache>(),
            currentUser: Substitute.For<ICurrentUser>(),
            descentLimiter: new HumanTask.HumanTaskDescentLimiter(
                Microsoft.Extensions.Options.Options.Create(new HumanTask.HumanTaskFunctionOptions())),
            transitionSchemaResolver: NSubstitute.Substitute.For<BBT.Workflow.Execution.Validation.ITransitionSchemaResolver>(),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }
}
