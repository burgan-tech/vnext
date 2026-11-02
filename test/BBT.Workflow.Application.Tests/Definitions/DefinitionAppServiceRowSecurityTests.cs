using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Authorization.RowSecurity;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions.CastHandlers;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// The publish path runs the schema- and key-aware row-security check (<see cref="RowSecurityPublishCheck"/>)
/// for flows that declare <c>rowSecurity</c>: errors refuse the publish before anything is persisted,
/// warnings never change the response.
/// </summary>
public sealed class DefinitionAppServiceRowSecurityTests
{
    private const string SchemaKey = "rs-schema";

    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceDataWriteService _dataWriter = Substitute.For<IInstanceDataWriteService>();
    private readonly DefinitionAppService _sut;

    public DefinitionAppServiceRowSecurityTests()
    {
        var guids = Substitute.For<IGuidGenerator>();
        guids.Create().Returns(_ => Guid.NewGuid());

        var services = new ServiceCollection();
        services.AddLogging();
        // ApplicationService.Logger / GuidGenerator resolve through ILazyServiceProvider.
        services.AddTransient<ILazyServiceProvider, LazyServiceProvider>();
        services.AddSingleton(guids);
        services.AddSingleton(Substitute.For<ICurrentSchema>());
        services.AddSingleton(Substitute.For<ISchemaMigrationOrchestrator>());
        var provider = services.BuildServiceProvider();

        var castHandler = Substitute.For<IWorkflowCastHandler>();
        castHandler.CanHandle(Arg.Any<string>()).Returns(true);

        _sut = new DefinitionAppService(
            Substitute.For<ICurrentSchema>(),
            Substitute.For<IRuntimeInfoProvider>(),
            _instanceRepository,
            _dataWriter,
            new ComponentValidatorProcessor(
                [new FlowComponentValidator(new WorkflowValidator())],
                Substitute.For<IScriptAssemblyCatalog>()),
            new WorkflowCastProcessor([castHandler]),
            Substitute.For<IDomainCacheContext>(),
            new RowSecurityPublishCheck(_cache, NullLogger<RowSecurityPublishCheck>.Instance),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider);
    }

    private static PublishInput FlowInput(string? rowSecurityJson)
    {
        var json = $$"""
            {
                "type": "F",
                "labels": [{"label": "Test", "language": "en"}],
                "schema": {"key": "{{SchemaKey}}", "domain": "test-domain", "flow": "sys-schemas", "version": "1.0.0"},
                "states": [
                    {
                        "key": "review",
                        "stateType": 2,
                        "versionStrategy": "Minor",
                        "labels": [{"label": "Review", "language": "en"}],
                        "transitions": []
                    }
                ],
                "sharedTransitions": [],
                "startTransition": {
                    "key": "start",
                    "target": "review",
                    "versionStrategy": "Minor",
                    "triggerType": 0,
                    "labels": [{"label": "Start", "language": "en"}]
                }
                {{(rowSecurityJson is null ? "" : ",\"rowSecurity\":" + rowSecurityJson)}}
            }
            """;

        return new PublishInput
        {
            Key = "rs-a",
            Domain = "test-domain",
            Flow = RuntimeSysSchemaInfo.Flows,
            FlowVersion = "1.0.0",
            Version = "1.0.0",
            Attributes = JsonDocument.Parse(json).RootElement.Clone()
        };
    }

    private void SchemaResolves(string schemaJson)
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            $$"""{ "type": "master", "schema": {{schemaJson}} }""", JsonSerializerConstants.JsonOptions)!;
        schema.SetReference(new Reference(SchemaKey, "test-domain", "sys-schemas", "1.0.0"));
        _cache.GetSchemaAsync(Arg.Any<string>(), SchemaKey, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
    }

    private const string RuleOnIban =
        """{"rules":[{"roles":["officer"],"filter":{"attributes":{"iban":{"eq":"x"}}}}]}""";

    [Fact]
    public async Task Publish_WithMaskedPathRule_FailsValidation_AndPersistsNothing()
    {
        SchemaResolves("""{"type":"object","properties":{"iban":{"type":"string","x-masking":{"operator":"mask"}}}}""");

        var result = await _sut.PublishAsync(FlowInput(RuleOnIban));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InvalidWorkflow);
        await _instanceRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
        await _dataWriter.DidNotReceiveWithAnyArgs()
            .AppendExplicitAsync(default!, default, default!, default!, default);
    }

    [Fact]
    public async Task Publish_WhenSchemaUnresolvable_Succeeds_WithWarningOnly()
    {
        _cache.GetSchemaAsync(Arg.Any<string>(), SchemaKey, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.NotFound("Cache:300001", "Schema not found")));

        var result = await _sut.PublishAsync(FlowInput(RuleOnIban));

        result.IsSuccess.ShouldBeTrue();
        await _instanceRepository.ReceivedWithAnyArgs(1).InsertAsync(default!, default, default);
    }

    [Fact]
    public async Task Publish_WithoutRowSecurity_DoesNotReadTheSchema()
    {
        var result = await _sut.PublishAsync(FlowInput(null));

        result.IsSuccess.ShouldBeTrue();
        _cache.ReceivedCalls().ShouldBeEmpty();
    }
}
