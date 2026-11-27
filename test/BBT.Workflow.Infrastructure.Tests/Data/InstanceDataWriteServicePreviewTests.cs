using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Caching;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// The side-effect-free append preview used at transition intake (vnext#1255): the same merge the
/// write funnel performs, validated against the master schema with the real validator, without the
/// database. It refuses only what the funnel would refuse and accepts whenever it cannot reproduce
/// what the funnel validates.
/// </summary>
public class InstanceDataWriteServicePreviewTests
{
    private const string ClosedSchema =
        """
        {
          "type": "object",
          "properties": { "code": { "type": "string" }, "name": { "type": "string" } },
          "required": ["code"],
          "additionalProperties": false
        }
        """;

    private const string OpenSchema =
        """{ "type": "object", "properties": { "code": { "type": "string" } }, "required": ["code"] }""";

    private readonly IAetherDbContextProvider<WorkflowDbContext> _dbContextProvider =
        Substitute.For<IAetherDbContextProvider<WorkflowDbContext>>();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IComponentCacheStore _componentCache = Substitute.For<IComponentCacheStore>();
    private readonly WorkflowExecutionOptions _options = new();

    public InstanceDataWriteServicePreviewTests()
    {
        _serviceProvider.GetService(typeof(IComponentCacheStore)).Returns(_componentCache);
        UseSchema(ClosedSchema);
    }

    [Fact]
    public async Task ClosedSchema_ExtraField_IsRefused_NamingTheField()
    {
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"actor":"someone"}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeFalse();
        result.Error.ValidationErrors.ShouldNotBeNull();
        result.Error.ValidationErrors!.SelectMany(e => e.MemberNames).ShouldContain("actor");
    }

    [Fact]
    public async Task Refusal_CarriesTheSameDetailShape_AsTheTransitionSchemaLayer()
    {
        // The HTTP mapper picks the error.validationErrors response from Error.Detail; without it the
        // two intake layers would answer one failure in two formats.
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"actor":"someone"}"""), CreateWorkflow(), culture: "tr-TR");

        var details = JsonSerializer.Deserialize<SchemaValidationProblemDetails>(
            result.Error.Detail!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        details.Errors.ShouldContain(e => e.Path == "actor");
    }

    [Fact]
    public async Task PreviewThatThrows_IsAccepted_AndLeftToTheFunnel()
    {
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");
        _componentCache.GetSchemaAsync("test-domain", "master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns<Task<Result<SchemaDefinition>>>(_ => throw new InvalidOperationException("cache down"));

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"actor":"someone"}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");
        _componentCache.GetSchemaAsync("test-domain", "master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns<Task<Result<SchemaDefinition>>>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"actor":"someone"}"""), CreateWorkflow()));
    }

    [Fact]
    public async Task ClosedSchema_DeclaredField_IsAccepted()
    {
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"name":"Branch"}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenSchema_ExtraField_IsAccepted()
    {
        UseSchema(OpenSchema);
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"actor":"someone"}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task TheMergedDocumentIsValidated_NotTheBodyAlone()
    {
        // `code` is required and only the stored head carries it.
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"name":"Branch"}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task NoStoredData_TheBodyIsTheWholeDocument()
    {
        // The start path: nothing to merge onto, so a missing required field is refused.
        var instance = InstanceFactory.CreateDefault();

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"name":"Branch"}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeThatChangesNothing_IsAccepted_LikeTheAppendThatWritesNothing()
    {
        // The head already breaks the closed schema; a body that changes nothing is not what broke it,
        // and the funnel returns before validating a duplicate.
        var instance = await InstanceWithDataAsync("""{"code":"A1","legacy":true}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"legacy":true}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task BodyCarryingAFileNode_IsLeftToTheFunnel_WithoutLoadingTheSchema()
    {
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"passport":{"content":"aGk=","name":"p.pdf"}}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
        await _componentCache.DidNotReceiveWithAnyArgs().GetSchemaAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("""{"code":"A1","secret":"ENCRYPTED:AES256:abc"}""", """{"actor":"someone"}""")]
    [InlineData("""{"code":"A1"}""", """{"actor":"HASHED:SHA256:abc"}""")]
    public async Task ProtectedValuesInHeadOrBody_AreLeftToTheFunnel(string stored, string body)
    {
        var instance = await InstanceWithDataAsync(stored);

        var result = await CreateService().PreviewAppendAsync(instance, new JsonData(body), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task NonObjectBody_IsLeftToTheFunnel()
    {
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(instance, new JsonData("[1,2]"), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task SchemaThatCannotBeLoaded_IsNotValidated_AsInTheFunnel()
    {
        _componentCache.GetSchemaAsync("test-domain", "master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.Failure("schema:down", "down")));
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"actor":"someone"}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task WorkflowWithoutAMasterSchema_IsAccepted()
    {
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"actor":"someone"}"""), CreateWorkflow(withSchema: false));

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task KillSwitchOff_SkipsThePreview()
    {
        _options.InstanceDataWrite.IntakeMasterSchemaPreview = false;
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");

        var result = await CreateService().PreviewAppendAsync(
            instance, new JsonData("""{"actor":"someone"}"""), CreateWorkflow());

        result.IsSuccess.ShouldBeTrue();
        await _componentCache.DidNotReceiveWithAnyArgs().GetSchemaAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Preview_ChangesNothing_AndNeverTouchesTheDatabase()
    {
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");
        var before = instance.LatestData;
        var pending = instance.DataBuffer!.AccumulatedDelta!.Json;

        await CreateService().PreviewAppendAsync(instance, new JsonData("""{"name":"Branch"}"""), CreateWorkflow());
        await CreateService().PreviewAppendAsync(instance, new JsonData("""{"actor":"someone"}"""), CreateWorkflow());

        instance.LatestData.ShouldBeSameAs(before);
        instance.DataBuffer!.AccumulatedDelta!.Json.ShouldBe(pending);
        await _dbContextProvider.DidNotReceive().GetDbContextAsync();
    }

    [Fact]
    public async Task Preview_AndFunnel_ReachTheSameVerdict()
    {
        // The buffered append validates the merged content with the funnel's own check.
        var service = CreateService();
        var workflow = CreateWorkflow();
        var instance = await InstanceWithDataAsync("""{"code":"A1"}""");
        var body = new JsonData("""{"actor":"someone"}""");

        var preview = await service.PreviewAppendAsync(instance, body, workflow);
        var funnel = await Should.ThrowAsync<BBT.Workflow.Aspects.SchemaValidationException>(
            () => service.AppendAsync(instance, body, VersionStrategy.IncreasePatch, CancellationToken.None, workflow));

        preview.IsSuccess.ShouldBeFalse();
        funnel.ShouldNotBeNull();
    }

    private InstanceDataWriteService CreateService() => new(
        _dbContextProvider,
        _serviceProvider,
        new JsonSchemaValidator(),
        Options.Create(_options),
        NullLogger<InstanceDataWriteService>.Instance);

    /// <summary>
    /// An instance whose in-memory latest data is <paramref name="json"/>, seeded through the buffered
    /// (history: none) append so no database is involved.
    /// </summary>
    private async Task<Instance> InstanceWithDataAsync(string json)
    {
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        await CreateService().AppendAsync(instance, new JsonData(json), VersionStrategy.IncreaseMinor);
        return instance;
    }

    private void UseSchema(string schemaJson)
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            $$"""{ "type": "JSON", "schema": {{schemaJson}} }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference("master", "test-domain", "sys-schemas", "1.0.0"));
        _componentCache.GetSchemaAsync("test-domain", "master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
    }

    private static BBT.Workflow.Definitions.Workflow CreateWorkflow(bool withSchema = true)
    {
        var json = """
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           {"key": "state1", "stateType": "Intermediate", "transitions": []}
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<BBT.Workflow.Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        if (withSchema)
            workflow.SetSchema(new Reference("master", "test-domain", "sys-schemas", "1.0.0"));
        return workflow;
    }
}
