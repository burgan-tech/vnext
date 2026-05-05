using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Application.Pagination;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow;
using BBT.Workflow.Caching;
using BBT.Workflow.Authorization;
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
/// Verifies <see cref="InstanceFilteringOptions.EnforceMasterSchemaFiltering"/> controls propagation of <see cref="SchemaFilterContext"/> to the repository.
/// </summary>
public sealed class InstanceQueryAppServiceInstanceFilteringTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "wf-key";
    private const string FlowVersion = "1.0.0";

    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IPaginationLinkGenerator _paginationLinkGenerator = Substitute.For<IPaginationLinkGenerator>();
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceQueryAppServiceInstanceFilteringTests()
    {
        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(default!, default!)
            .ReturnsForAnyArgs(Task.FromResult(mockUoW));

        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        _ambientServiceProvider = services.BuildServiceProvider();

        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        _paginationLinkGenerator.Relative().Returns(_paginationLinkGenerator);
        _paginationLinkGenerator
            .GenerateLinks(Arg.Any<HateoasPagedList<GetInstanceOutput>>(), Arg.Any<string>())
            .Returns(_ => Substitute.For<PaginationLinks>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
    }

    [Fact]
    public async Task GetInstanceListAsync_When_enforcement_disabled_passes_null_schema_context_despite_resolved_schema()
    {
        var workflow = DeserializeWorkflow("""
            {
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [],
              "sharedTransitions": [],
              "extensions": [],
              "startTransition": {"key": "start", "target": "init"},
              "schema": {"key":"sch","domain":"test-domain","flow":"sys-schemas","version":"1.0.0"}
            }
            """);
        workflow.SetReference(new Reference(WorkflowKey, Domain, RuntimeSysSchemaInfo.Flows, FlowVersion));

        var schemaDefinition = DeserializeSchemaDefinition("""
            {
              "type": "workflow",
              "schema": {
                "type": "object",
                "properties": {
                  "amount": {
                    "type": "number",
                    "x-filterOperators": ["eq", "gt"]
                  }
                }
              }
            }
            """);
        schemaDefinition.SetReference(new Reference("sch", Domain, RuntimeSysSchemaInfo.Schemas, "1.0.0"));

        _componentCacheStore
            .GetFlowAsync(Domain, WorkflowKey, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Definitions.Workflow>.Ok(workflow)));
        _componentCacheStore
            .GetSchemaAsync(Domain, "sch", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SchemaDefinition>.Ok(schemaDefinition)));

        var emptyPage = new HateoasPagedList<Instance>([], 1, 10, false);
        _instanceRepository
            .GetPagedResultsWithGroupsAsync(
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Is<SchemaFilterContext?>(c => c == null))
            .Returns(_ => Task.FromResult((emptyPage, (List<GroupSummary>?)null)));

        var urlTemplateBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlTemplateBuilder.BuildInstanceListUrl(Domain, WorkflowKey).Returns("/route");

        var service = CreateService(
            urlTemplateBuilder,
            Options.Create(new InstanceFilteringOptions { EnforceMasterSchemaFiltering = false }));

        var input = new GetInstanceListInput { Domain = Domain, Workflow = WorkflowKey, Page = 1, PageSize = 10 };

        var result = await service.GetInstanceListAsync(input, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();

        await _instanceRepository.Received(1).GetPagedResultsWithGroupsAsync(
            1,
            10,
            null,
            null,
            null,
            null,
            Arg.Any<CancellationToken>(),
            Arg.Is<SchemaFilterContext?>(c => c == null));
    }

    [Fact]
    public async Task GetInstanceListAsync_When_enforcement_enabled_passes_non_null_schema_context_when_schema_resolves()
    {
        var workflow = DeserializeWorkflow("""
            {
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [],
              "sharedTransitions": [],
              "extensions": [],
              "startTransition": {"key": "start", "target": "init"},
              "schema": {"key":"sch","domain":"test-domain","flow":"sys-schemas","version":"1.0.0"}
            }
            """);
        workflow.SetReference(new Reference(WorkflowKey, Domain, RuntimeSysSchemaInfo.Flows, FlowVersion));

        var schemaDefinition = DeserializeSchemaDefinition("""
            {
              "type": "workflow",
              "schema": {
                "type": "object",
                "properties": {
                  "amount": {
                    "type": "number",
                    "x-filterOperators": ["eq", "gt"]
                  }
                }
              }
            }
            """);
        schemaDefinition.SetReference(new Reference("sch", Domain, RuntimeSysSchemaInfo.Schemas, "1.0.0"));

        _componentCacheStore
            .GetFlowAsync(Domain, WorkflowKey, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Definitions.Workflow>.Ok(workflow)));
        _componentCacheStore
            .GetSchemaAsync(Domain, "sch", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SchemaDefinition>.Ok(schemaDefinition)));

        var emptyPage = new HateoasPagedList<Instance>([], 1, 10, false);
        _instanceRepository
            .GetPagedResultsWithGroupsAsync(
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Is<SchemaFilterContext?>(c => c != null))
            .Returns(_ => Task.FromResult((emptyPage, (List<GroupSummary>?)null)));

        var urlTemplateBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlTemplateBuilder.BuildInstanceListUrl(Domain, WorkflowKey).Returns("/route");

        var service = CreateService(
            urlTemplateBuilder,
            Options.Create(new InstanceFilteringOptions { EnforceMasterSchemaFiltering = true }));

        var input = new GetInstanceListInput { Domain = Domain, Workflow = WorkflowKey, Page = 1, PageSize = 10 };

        var result = await service.GetInstanceListAsync(input, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();

        await _instanceRepository.Received(1).GetPagedResultsWithGroupsAsync(
            1,
            10,
            null,
            null,
            null,
            null,
            Arg.Any<CancellationToken>(),
            Arg.Is<SchemaFilterContext?>(c => c != null));
    }

    private InstanceQueryAppService CreateService(
        IUrlTemplateBuilder urlTemplateBuilder,
        IOptions<InstanceFilteringOptions> instanceFilteringOptions)
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

        var instanceExtensionService = Substitute.For<IInstanceExtensionService>();
        instanceExtensionService.ProcessExtensionsAsync(
                Arg.Any<string[]?>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<Definitions.Workflow>(),
                Arg.Any<ExtensionScope>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<Dictionary<string, object>>.Ok(new Dictionary<string, object>()));

        return new InstanceQueryAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: _runtimeInfoProvider,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceTransitionRepository: Substitute.For<IInstanceTransitionRepository>(),
            instanceCorrelationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            instanceExtensionService: instanceExtensionService,
            scriptContextFactory: scriptContextFactory,
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: urlTemplateBuilder,
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            paginationLinkGenerator: _paginationLinkGenerator,
            instanceFilteringOptions: instanceFilteringOptions,
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    private static Definitions.Workflow DeserializeWorkflow(string json) =>
        JsonSerializer.Deserialize<Definitions.Workflow>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static SchemaDefinition DeserializeSchemaDefinition(string json) =>
        JsonSerializer.Deserialize<SchemaDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}
