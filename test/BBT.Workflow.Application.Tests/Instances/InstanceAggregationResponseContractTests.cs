using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Net;
using BBT.Workflow.Headers;
using BBT.Workflow.Orchestration.Controllers.Instances;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using System.Threading;
using BBT.Workflow.Definitions.GraphQL;
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

namespace BBT.Workflow.Instances;

/// <summary>
/// Guards the existing list/group HTTP contract while aggregation internals are optimized. The repository is substituted; SQL is covered separately.
/// </summary>
public sealed class InstanceAggregationResponseContractTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "wf-key";
    private const string AggregateQuery = """{"count":true,"sum":"attributes.amount","avg":"attributes.amount","min":"attributes.amount","max":"attributes.amount"}""";
    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IPaginationLinkGenerator _paginationLinkGenerator = Substitute.For<IPaginationLinkGenerator>();
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceAggregationResponseContractTests()
    {
        var manager = Substitute.For<IUnitOfWorkManager>();
        manager.BeginAsync(default!, default!).ReturnsForAnyArgs(Task.FromResult(Substitute.For<IUnitOfWork>()));
        _ambientServiceProvider = new ServiceCollection().AddSingleton(manager).BuildServiceProvider();
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;
        _paginationLinkGenerator.Relative().Returns(_paginationLinkGenerator);
        _paginationLinkGenerator.GenerateLinks(Arg.Any<HateoasPagedList<GetInstanceOutput>>(), Arg.Any<string>())
            .Returns(_ => Substitute.For<PaginationLinks>());
        _paginationLinkGenerator.GenerateLinks(Arg.Any<HateoasPagedList<GroupSummary>>(), Arg.Any<string>())
            .Returns(_ => Substitute.For<PaginationLinks>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneAggregation_PreservesLegacyResponseWithoutTopLevelValues(bool envelope)
    {
        ConfigureResult();
        using var json = await InvokeAndSerializeAsync(envelope, AggregateQuery);
        json.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
        json.RootElement.TryGetProperty("links", out _).ShouldBeTrue();
        json.RootElement.TryGetProperty("aggregations", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task OrdinaryList_OmitsOptionalAggregationProperty()
    {
        ConfigureResult();
        using var json = await InvokeAndSerializeAsync(false, null);
        json.RootElement.TryGetProperty("aggregations", out _).ShouldBeFalse();
        json.RootElement.TryGetProperty("links", out _).ShouldBeTrue();
        json.RootElement.GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupResponse_KeepsGroupsInItems_WithoutTopLevelAggregation(bool empty)
    {
        var groups = empty ? new List<GroupSummary>() : new List<GroupSummary>
        {
            new() { Name = "retail", Keys = new Dictionary<string, object?> { ["attributes.category"] = "retail" }, Count = 4, Sum = 10.5m }
        };
        ConfigureResult(groups);
        using var json = await InvokeAndSerializeAsync(false, null,
            """{"field":"attributes.category","aggregations":{"count":true,"sum":"attributes.amount"}}""");
        json.RootElement.TryGetProperty("aggregations", out _).ShouldBeFalse();
        var items = json.RootElement.GetProperty("items");
        items.GetArrayLength().ShouldBe(empty ? 0 : 1);
        if (!empty)
        {
            items[0].GetProperty("count").GetInt64().ShouldBe(4);
            items[0].GetProperty("sum").GetDecimal().ShouldBe(10.5m);
            items[0].GetProperty("keys").GetProperty("attributes.category").GetString().ShouldBe("retail");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMvcListEndpoint_PreservesLegacyAggregationResponse(bool envelope)
    {
        ConfigureResult();
        var urls = Substitute.For<IUrlTemplateBuilder>();
        urls.BuildInstanceListUrl(Domain, WorkflowKey).Returns("/route");
        var service = CreateService(urls);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAetherApiVersioning(apiTitle: "Aggregation contract tests");
        builder.Services.AddControllers().AddApplicationPart(typeof(InstanceController).Assembly);
        builder.Services.AddSingleton<IInstanceQueryAppService>(service);
        builder.Services.AddSingleton(Substitute.For<IHeaderService>());
        builder.Services.AddTransient<ResponseHeaderFilter>();
        // Unused command/subflow collaborators are substituted; the real query application runs.
        foreach (var parameter in typeof(InstanceController).GetConstructors().Single().GetParameters())
        {
            if (parameter.ParameterType != typeof(IInstanceQueryAppService))
                builder.Services.AddSingleton(parameter.ParameterType, Substitute.For([parameter.ParameterType], []));
        }
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var previous = AmbientServiceProvider.Current;
            AmbientServiceProvider.Current = _ambientServiceProvider;
            try { await next(context); }
            finally { AmbientServiceProvider.Current = previous; }
        });
        app.MapControllers();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var query = envelope ? "filter=" + Uri.EscapeDataString("{\"aggregations\":" + AggregateQuery + "}")
            : "aggregations=" + Uri.EscapeDataString(AggregateQuery);
        using var response = await client.GetAsync($"/api/v1/{Domain}/workflows/{WorkflowKey}/instances?{query}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.TryGetProperty("aggregations", out _).ShouldBeFalse();
        json.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    private void ConfigureResult(List<GroupSummary>? groups = null)
    {
        var page = new HateoasPagedList<Instance>([], 1, 10, false);
        _instanceRepository.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<SchemaFilterContext?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((page, groups)));
        _instanceRepository.GetPagedResultsWithGroupsAsync(Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<GraphQLFilterRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((page, groups)));
    }

    private async Task<JsonDocument> InvokeAndSerializeAsync(bool envelope, string? aggregation, string? groupBy = null)
    {
        var urls = Substitute.For<IUrlTemplateBuilder>();
        urls.BuildInstanceListUrl(Domain, WorkflowKey).Returns("/route");
        var input = new GetInstanceListInput
        {
            Domain = Domain, Workflow = WorkflowKey, Page = 1, PageSize = 10,
            Filter = envelope ? "{\"aggregations\":" + aggregation + "}" : null,
            QueryParameters = new Dictionary<string, string?>()
        };
        if (!envelope && aggregation != null) input.QueryParameters["aggregations"] = aggregation;
        if (groupBy != null) input.QueryParameters["groupBy"] = groupBy;
        var result = await CreateService(urls).GetInstanceListAsync(input, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        return JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
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
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: scriptContextFactory,
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: urlTemplateBuilder,
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            callerRoleResolver: Substitute.For<ICallerRoleResolver>(),
            paginationLinkGenerator: _paginationLinkGenerator,
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }
}
