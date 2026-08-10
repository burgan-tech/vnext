using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Scripting;
using BBT.Workflow.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

public sealed class InstanceDataMutationServiceTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-flow";
    private const string SchemaKey = "master-data";
    private const string Version = "1.0.0";

    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IJsonSchemaValidator _schemaValidator = Substitute.For<IJsonSchemaValidator>();

    [Fact]
    public async Task AddDataAsync_WhenMasterSchemaCannotBeLoaded_FailsWithoutMutatingInstance()
    {
        var workflow = CreateWorkflowWithSchema();
        var instance = CreateInstanceWithExistingData();
        var originalLatest = instance.LatestData!;
        var schemaError = Error.NotFound("schema.notfound", "Master schema could not be loaded");

        _componentCacheStore
            .GetSchemaAsync(Domain, SchemaKey, Version, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(schemaError));

        var service = new InstanceDataMutationService(_componentCacheStore, _schemaValidator);

        var result = await service.AddDataAsync(
            workflow,
            instance,
            Guid.NewGuid(),
            new JsonData("""{"newValue":42}"""),
            cancellationToken: CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(schemaError);
        instance.DataList.Count.ShouldBe(1);
        instance.LatestData.ShouldBeSameAs(originalLatest);
        originalLatest.Data.JsonElement.GetProperty("existing").GetString().ShouldBe("kept");
        _schemaValidator.DidNotReceive()
            .Validate(
                Arg.Any<JsonElement>(),
                Arg.Any<JsonElement?>(),
                Arg.Any<SchemaValidationOptions>());
    }

    [Fact]
    public async Task AddDataAsync_WhenMergedCandidateIsInvalid_ValidatesFullCandidateBeforeMutation()
    {
        var workflow = CreateWorkflowWithSchema();
        var instance = CreateInstanceWithExistingData();
        var originalLatest = instance.LatestData!;
        var schema = CreateSchemaDefinition();
        var validationError = Error.Validation("schema.invalid", "Candidate is invalid");
        JsonElement? validatedCandidate = null;
        var dataCountAtValidation = -1;

        _componentCacheStore
            .GetSchemaAsync(Domain, SchemaKey, Version, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
        _schemaValidator
            .Validate(
                Arg.Any<JsonElement>(),
                Arg.Any<JsonElement?>(),
                Arg.Any<SchemaValidationOptions>())
            .Returns(callInfo =>
            {
                validatedCandidate = callInfo.ArgAt<JsonElement?>(1);
                dataCountAtValidation = instance.DataList.Count;
                return Result.Fail(validationError);
            });

        var service = new InstanceDataMutationService(_componentCacheStore, _schemaValidator);

        var result = await service.AddDataAsync(
            workflow,
            instance,
            Guid.NewGuid(),
            new JsonData("""{"newValue":42}"""),
            cancellationToken: CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(validationError);
        dataCountAtValidation.ShouldBe(1);
        validatedCandidate.ShouldNotBeNull();
        validatedCandidate.Value.GetProperty("existing").GetString().ShouldBe("kept");
        validatedCandidate.Value.GetProperty("newValue").GetInt32().ShouldBe(42);
        instance.DataList.Count.ShouldBe(1);
        instance.LatestData.ShouldBeSameAs(originalLatest);
    }

    [Fact]
    public async Task AddDataAsync_WhenPayloadEqualsLatestSnapshot_SkipsSchemaAndVersionAppend()
    {
        var workflow = CreateWorkflowWithSchema();
        var instance = CreateInstanceWithExistingData();
        var originalLatest = instance.LatestData!;
        var service = new InstanceDataMutationService(_componentCacheStore, _schemaValidator);

        var result = await service.AddDataAsync(
            workflow,
            instance,
            Guid.NewGuid(),
            new JsonData("""{"existing":"kept"}"""),
            cancellationToken: CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(originalLatest);
        instance.DataList.Count.ShouldBe(1);
        await _componentCacheStore.DidNotReceive()
            .GetSchemaAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        _schemaValidator.DidNotReceiveWithAnyArgs()
            .Validate(default, default, default!);
    }

    [Fact]
    public async Task AddDataAsync_WhenDeltaDoesNotChangeMergedSnapshot_SkipsSchemaAndVersionAppend()
    {
        var workflow = CreateWorkflowWithSchema();
        var instance = CreateInstanceWithExistingData();
        instance.AddData(
            Guid.NewGuid(),
            new JsonData("""{"other":42}"""),
            VersionStrategy.IncreasePatch);
        var originalLatest = instance.LatestData!;
        var originalCount = instance.DataList.Count;
        var service = new InstanceDataMutationService(_componentCacheStore, _schemaValidator);

        var result = await service.AddDataAsync(
            workflow,
            instance,
            Guid.NewGuid(),
            new JsonData("""{"other":42}"""),
            cancellationToken: CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(originalLatest);
        instance.DataList.Count.ShouldBe(originalCount);
        await _componentCacheStore.DidNotReceiveWithAnyArgs()
            .GetSchemaAsync(default!, default!, default!, default);
        _schemaValidator.DidNotReceiveWithAnyArgs()
            .Validate(default, default, default!);
    }

    [Fact]
    public async Task ApplyScriptContextChangesAsync_WhenAnySnapshotIsInvalid_DoesNotMutateLiveInstance()
    {
        var workflow = CreateWorkflowWithSchema();
        var liveInstance = CreateInstanceWithExistingData();
        var originalLatest = liveInstance.LatestData!;
        var scriptInstance = CreateScriptInstanceWithTwoNewVersions(liveInstance);
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetWorkflow(workflow)
            .SetInstance(scriptInstance)
            .Build();
        scriptContext.Mutations.SetStage("must-not-be-applied");
        var transitionContext = CreateTransitionContext(workflow, liveInstance);
        var schema = CreateSchemaDefinition();
        var validationError = Error.Validation("schema.invalid", "Second snapshot is invalid");
        var validationCount = 0;

        _componentCacheStore
            .GetSchemaAsync(Domain, SchemaKey, Version, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
        _schemaValidator
            .Validate(
                Arg.Any<JsonElement>(),
                Arg.Any<JsonElement?>(),
                Arg.Any<SchemaValidationOptions>())
            .Returns(_ => ++validationCount == 2 ? Result.Fail(validationError) : Result.Ok());

        var service = new InstanceDataMutationService(_componentCacheStore, _schemaValidator);
        var result = await service.ApplyScriptContextChangesAsync(
            workflow,
            transitionContext,
            scriptContext,
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(validationError);
        validationCount.ShouldBe(2);
        liveInstance.DataList.Count.ShouldBe(1);
        liveInstance.LatestData.ShouldBeSameAs(originalLatest);
        liveInstance.Stage.ShouldBeNull();
        await _componentCacheStore.Received(1)
            .GetSchemaAsync(Domain, SchemaKey, Version, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyScriptContextChangesAsync_WhenBatchIsValid_ImportsVersionsAndUsesOneSchemaLookup()
    {
        var workflow = CreateWorkflowWithSchema();
        var liveInstance = CreateInstanceWithExistingData();
        var scriptInstance = CreateScriptInstanceWithTwoNewVersions(liveInstance);
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetWorkflow(workflow)
            .SetInstance(scriptInstance)
            .Build();
        scriptContext.Mutations.SetStage("reviewed");
        var transitionContext = CreateTransitionContext(workflow, liveInstance);
        var schema = CreateSchemaDefinition();

        _componentCacheStore
            .GetSchemaAsync(Domain, SchemaKey, Version, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
        _schemaValidator
            .Validate(
                Arg.Any<JsonElement>(),
                Arg.Any<JsonElement?>(),
                Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        var service = new InstanceDataMutationService(_componentCacheStore, _schemaValidator);
        var result = await service.ApplyScriptContextChangesAsync(
            workflow,
            transitionContext,
            scriptContext,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        liveInstance.DataList.Count.ShouldBe(3);
        liveInstance.Stage.ShouldBe("reviewed");
        liveInstance.LatestData!.Data.JsonElement.GetProperty("second").GetBoolean().ShouldBeTrue();
        transitionContext.Data.ShouldNotBeNull();
        _schemaValidator.Received(2)
            .Validate(
                Arg.Any<JsonElement>(),
                Arg.Any<JsonElement?>(),
                Arg.Any<SchemaValidationOptions>());
        await _componentCacheStore.Received(1)
            .GetSchemaAsync(Domain, SchemaKey, Version, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyScriptContextChangesAsync_WhenVersionWasValidatedOnTaskOutput_DoesNotValidateItTwice()
    {
        var workflow = CreateWorkflowWithSchema();
        var liveInstance = CreateInstanceWithExistingData();
        var scriptInstance = Instance.Create(liveInstance.Id, WorkflowKey, Version);
        scriptInstance.AddData(
            liveInstance.LatestData!.Id,
            new JsonData(liveInstance.LatestData.Data.Json));
        var schema = CreateSchemaDefinition();
        _componentCacheStore
            .GetSchemaAsync(Domain, SchemaKey, Version, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
        _schemaValidator
            .Validate(
                Arg.Any<JsonElement>(),
                Arg.Any<JsonElement?>(),
                Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        var service = new InstanceDataMutationService(_componentCacheStore, _schemaValidator);
        var addResult = await service.AddDataAsync(
            workflow,
            scriptInstance,
            Guid.NewGuid(),
            new JsonData("""{"taskOutput":true}"""),
            VersionStrategy.IncreasePatch,
            CancellationToken.None,
            new Dictionary<string, string?> { ["accept-language"] = "tr-TR" });
        addResult.IsSuccess.ShouldBeTrue();

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetWorkflow(workflow)
            .SetInstance(scriptInstance)
            .Build();
        var transitionContext = CreateTransitionContext(workflow, liveInstance);

        var applyResult = await service.ApplyScriptContextChangesAsync(
            workflow,
            transitionContext,
            scriptContext,
            CancellationToken.None);

        applyResult.IsSuccess.ShouldBeTrue();
        _schemaValidator.Received(1)
            .Validate(
                Arg.Any<JsonElement>(),
                Arg.Any<JsonElement?>(),
                Arg.Any<SchemaValidationOptions>());
        await _componentCacheStore.Received(1)
            .GetSchemaAsync(Domain, SchemaKey, Version, Arg.Any<CancellationToken>());
    }

    private static Definitions.Workflow CreateWorkflowWithSchema()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", Version));
        workflow.SetSchema(new Reference(SchemaKey, Domain, "sys-schemas", Version));
        return workflow;
    }

    private static Instance CreateInstanceWithExistingData()
    {
        var instance = Instance.Create(Guid.NewGuid(), WorkflowKey, Version);
        instance.AddData(Guid.NewGuid(), new JsonData("""{"existing":"kept"}"""));
        return instance;
    }

    private static Instance CreateScriptInstanceWithTwoNewVersions(Instance liveInstance)
    {
        var original = liveInstance.LatestData!;
        var scriptInstance = Instance.Create(liveInstance.Id, WorkflowKey, Version);
        scriptInstance.AddDataWithVersion(
            original.Id,
            new JsonData(original.Data.Json),
            original.Version);
        scriptInstance.AddDataWithVersion(
            Guid.NewGuid(),
            new JsonData("""{"existing":"kept","first":true}"""),
            "1.0.1");
        scriptInstance.AddDataWithVersion(
            Guid.NewGuid(),
            new JsonData("""{"existing":"kept","first":true,"second":true}"""),
            "1.0.2");
        return scriptInstance;
    }

    private static TransitionExecutionContext CreateTransitionContext(
        Definitions.Workflow workflow,
        Instance instance)
        => new()
        {
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            InstanceId = instance.Id,
            TransitionKey = "test-transition",
            Workflow = workflow,
            Instance = instance,
            Headers = new Dictionary<string, string?> { ["accept-language"] = "tr-TR" },
            Data = instance.Data
        };

    private static SchemaDefinition CreateSchemaDefinition()
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            """{"type":"JSON","schema":{"type":"object"}}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference(SchemaKey, Domain, "sys-schemas", Version));
        return schema;
    }
}
