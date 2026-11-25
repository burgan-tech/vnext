using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Policies;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Validation;
using BBT.Workflow.Versions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Validation;

/// <summary>
/// The master-schema preview at transition intake (vnext#1255): which requests are previewed against
/// the flow's master schema before admission, and that a refusal is returned as the validation result.
/// What the preview itself decides is covered by <c>InstanceDataWriteServicePreviewTests</c>.
/// </summary>
public class TransitionValidationServiceMasterSchemaPreviewTests
{
    private static readonly Error MasterSchemaFailure =
        Error.Validation("Validation:Failed", "JSON schema validation failed");

    private readonly IJsonSchemaValidator _schemaValidator = Substitute.For<IJsonSchemaValidator>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly ITransitionSchemaResolver _schemaResolver = Substitute.For<ITransitionSchemaResolver>();
    private readonly IInstanceDataWriteService _dataWriteService = Substitute.For<IInstanceDataWriteService>();
    private readonly ITransitionSpecification _policy = Substitute.For<ITransitionSpecification>();
    private readonly TransitionValidationService _service;

    public TransitionValidationServiceMasterSchemaPreviewTests()
    {
        _policy.IsApplicable(default!).ReturnsForAnyArgs(true);
        _policy.IsSatisfiedBy(default!).ReturnsForAnyArgs(Result.Ok());
        _schemaResolver
            .ResolveAsync(default!, default!, default!, default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(Result<Reference?>.Ok(call.ArgAt<Transition>(2)?.Schema)));
        _dataWriteService
            .PreviewAppendAsync(default!, default!, default!, default, default)
            .ReturnsForAnyArgs(Result.Ok());

        var composite = new CompositeTransitionSpecification(
            new[] { _policy },
            Substitute.For<ILogger<CompositeTransitionSpecification>>());
        _service = new TransitionValidationService(
            new TransitionExecutionPolicy(composite),
            _schemaValidator,
            _componentCacheStore,
            Substitute.For<IVersionRegistryWriteGuard>(),
            _schemaResolver,
            _dataWriteService);
    }

    [Fact]
    public async Task ManualRequest_IsPreviewedAgainstTheMasterSchema_WithItsBodyAndCulture()
    {
        var context = CreateContext(headers: new Dictionary<string, string?> { ["Accept-Language"] = "tr-TR" });

        var result = await _service.ValidateAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _dataWriteService.Received(1).PreviewAppendAsync(
            context.Instance,
            Arg.Is<JsonData>(d => d.JsonElement.GetProperty("actor").GetString() == "someone"),
            context.Workflow,
            "tr-TR",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewRefusal_IsReturned_AsTheValidationResult()
    {
        _dataWriteService
            .PreviewAppendAsync(default!, default!, default!, default, default)
            .ReturnsForAnyArgs(Result.Fail(MasterSchemaFailure));

        var result = await _service.ValidateAsync(CreateContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(MasterSchemaFailure);
    }

    [Fact]
    public async Task TransitionSchemaFailure_WinsAndSkipsThePreview()
    {
        var schemaRef = new Reference("transition-schema", "test-domain", "sys-schemas", "1.0.0");
        var context = CreateContext();
        typeof(Transition).GetProperty(nameof(Transition.Schema))!.SetValue(context.Transition, schemaRef);
        var transitionFailure = Error.Validation("Validation:Failed", "transition schema");
        _componentCacheStore
            .GetSchemaAsync(schemaRef.Domain, schemaRef.Key, schemaRef.Version, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(CreateSchemaDefinition(schemaRef)));
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Fail(transitionFailure));

        var result = await _service.ValidateAsync(context, CancellationToken.None);

        result.Error.ShouldBe(transitionFailure);
        await AssertNotPreviewedAsync();
    }

    [Fact]
    public async Task PolicyRefusal_WinsAndSkipsThePreview()
    {
        // The preview reads the stored document: a request the state machine refuses (wrong state,
        // unauthorized actor) must not reach it, nor be answered with a master-schema message.
        var policyFailure = Error.Forbidden("Transition:Denied", "not allowed");
        _policy.IsSatisfiedBy(default!).ReturnsForAnyArgs(Result.Fail(policyFailure));

        var result = await _service.ValidateAsync(CreateContext(), CancellationToken.None);

        result.Error.ShouldBe(policyFailure);
        await AssertNotPreviewedAsync();
    }

    [Fact]
    public async Task TransitionSchemaValidationAlone_DoesNotPreview()
    {
        var result = await _service.ValidateSchemaAsync(CreateContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await AssertNotPreviewedAsync();
    }

    [Fact]
    public async Task TransitionWithAMapping_IsExempt()
    {
        var context = CreateContext();
        context.Transition!.SetMapping(new ScriptCode(
            location: "inline", code: "mapping", type: MappingType.Local, encoding: CodeEncoding.Native));

        var result = await _service.ValidateAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await AssertNotPreviewedAsync();
    }

    [Fact]
    public async Task OutputBlock_SkipsSchemaPreview()
    {
        // A declarative output block decides what is written, as a mapping script does: the request is not previewed.
        var context = CreateContext();
        BBT.Workflow.Application.Tests.Execution.Transitions.TransitionDataMapperDeclarativeTests.SetOutput(
            context.Transition!,
            BBT.Workflow.Application.Tests.Execution.Transitions.TransitionDataMapperDeclarativeTests.Block(set: [("$.x", "input.actor")]));

        var result = await _service.ValidateAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await AssertNotPreviewedAsync();
    }

    [Theory]
    [InlineData(TriggerType.Automatic)]
    [InlineData(TriggerType.Scheduled)]
    [InlineData(TriggerType.Event)]
    public async Task SystemTriggeredHop_IsNotPreviewed(TriggerType trigger)
    {
        var result = await _service.ValidateAsync(CreateContext(trigger: trigger), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await AssertNotPreviewedAsync();
    }

    [Fact]
    public async Task FlowWithoutAMasterSchema_IsNotPreviewed()
    {
        var result = await _service.ValidateAsync(CreateContext(withMasterSchema: false), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await AssertNotPreviewedAsync();
    }

    [Fact]
    public async Task RequestWithoutABody_IsNotPreviewed()
    {
        var result = await _service.ValidateAsync(CreateContext(data: null), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await AssertNotPreviewedAsync();
    }

    [Fact]
    public async Task PolicyOnlyValidation_DoesNotPreview()
    {
        // A job re-entry or the start pipeline carries a payload the intake already validated.
        var result = await _service.ValidatePolicyAsync(CreateContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await AssertNotPreviewedAsync();
    }

    private Task AssertNotPreviewedAsync() =>
        _dataWriteService.DidNotReceiveWithAnyArgs().PreviewAppendAsync(default!, default!, default!, default, default);

    private static TransitionExecutionContext CreateContext(
        TriggerType trigger = TriggerType.Manual,
        bool withMasterSchema = true,
        object? data = null,
        IReadOnlyDictionary<string, string?>? headers = null,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(data))] string? dataExpression = null)
    {
        var instanceId = Guid.NewGuid();
        var workflow = CreateWorkflow(withMasterSchema);

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "activate",
            Trigger = trigger,
            Actor = trigger == TriggerType.Manual ? ExecutionActor.User : ExecutionActor.System,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Transition = Transition.Create(
                "activate", null, "state1", TriggerType.Manual, VersionStrategy.IncreasePatch.Code),
            Instance = Instance.Create(instanceId, "test-workflow", "1.0.0", "test-workflow"),
            // An omitted argument means "the default body"; an explicit null means "no body".
            Data = dataExpression is null ? JsonSerializer.SerializeToElement(new { actor = "someone" }) : data,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16],
            Headers = headers ?? new Dictionary<string, string?>()
        };
    }

    private static Definitions.Workflow CreateWorkflow(bool withMasterSchema)
    {
        var json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [ { "key": "state1", "type": "P", "transitions": [] } ],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        })!;
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        if (withMasterSchema)
            workflow.SetSchema(new Reference("master", "test-domain", "sys-schemas", "1.0.0"));
        return workflow;
    }

    private static SchemaDefinition CreateSchemaDefinition(Reference reference)
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            """{ "type": "workflow", "schema": { "type": "object" } }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(reference);
        return schema;
    }
}
