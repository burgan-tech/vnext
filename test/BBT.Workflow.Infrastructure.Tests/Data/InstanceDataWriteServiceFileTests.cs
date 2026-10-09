using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Files;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// x-storage defence in depth in the single InstanceData writer (spec §3): <c>content</c> produced by task outputs or
/// subflow output mappings is offloaded (Trusted) before the write gate and the row lock, so no binding I/O runs
/// under the lock and no bytes are ever persisted — buffered (<c>history: none</c>) appends included.
/// </summary>
public class InstanceDataWriteServiceFileTests
{
    private const string Delta = """{"passport":{"content":"aGk=","name":"p.pdf"}}""";
    private const string Handled = """{"passport":{"component":"vnext-blob-local","file":"f-1","name":"p.pdf"}}""";

    private readonly IAetherDbContextProvider<WorkflowDbContext> _dbContextProvider =
        Substitute.For<IAetherDbContextProvider<WorkflowDbContext>>();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IFileOffloadService _offload = Substitute.For<IFileOffloadService>();
    private readonly IComponentCacheStore _componentCache = Substitute.For<IComponentCacheStore>();
    private readonly IJsonSchemaValidator _validator = Substitute.For<IJsonSchemaValidator>();
    private readonly SchemaDefinition _schema = CreateSchema();

    public InstanceDataWriteServiceFileTests()
    {
        _serviceProvider.GetService(typeof(IFileOffloadService)).Returns(_offload);
        _serviceProvider.GetService(typeof(IComponentCacheStore)).Returns(_componentCache);
        _componentCache.GetSchemaAsync("test-domain", "master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(_schema));
        _validator.Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>()).Returns(Result.Ok());
        _offload.GetFields(_schema)
            .Returns((IReadOnlyList<FileStorageField>)[new FileStorageField(["passport"], "vnext-blob-local")]);
    }

    private InstanceDataWriteService CreateService() => new(
        _dbContextProvider,
        _serviceProvider,
        _validator,
        Options.Create(new WorkflowExecutionOptions()),
        NullLogger<InstanceDataWriteService>.Instance);

    [Fact]
    public async Task BufferedAppend_WithContent_StoresTheHandleNotTheBytes()
    {
        _offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<FileOffloadResult>.Ok(new FileOffloadResult(JsonDocument.Parse(Handled).RootElement.Clone(), true)));
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        var workflow = CreateWorkflow();

        var row = await CreateService().AppendAsync(instance, new JsonData(Delta), VersionStrategy.IncreaseMinor,
            CancellationToken.None, workflow);

        row.ShouldNotBeNull();
        row.Data.Json.ShouldNotContain("content");
        row.Data.Json.ShouldContain("f-1");
        instance.DataBuffer!.AccumulatedDelta!.Json.ShouldNotContain("content");
        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => r.Mode == FileOffloadMode.Trusted && r.InstanceId == instance.Id
                                            && r.Workflow == workflow && r.LatestData == null && r.Fields != null),
            Arg.Any<CancellationToken>());
        // One schema load per append, shared by x-storage and validation; the fields never reload it.
        await _componentCache.Received(1).GetSchemaAsync("test-domain", "master", "1.0.0", Arg.Any<CancellationToken>());
        await _offload.DidNotReceiveWithAnyArgs().GetFieldsAsync(default!, default);
    }

    [Fact]
    public async Task Append_StoreFailure_ThrowsFileStoreUnavailable_BeforeTouchingTheDatabase()
    {
        _offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<FileOffloadResult>.Fail(WorkflowErrors.FileStoreUnavailable("vnext-blob-local")));
        var instance = InstanceFactory.CreateDefault();

        var ex = await Should.ThrowAsync<FileStoreUnavailableException>(() => CreateService().AppendAsync(
            instance, new JsonData(Delta), VersionStrategy.IncreaseMinor, CancellationToken.None, CreateWorkflow()));

        ex.Message.ShouldContain("vnext-blob-local");
        await _dbContextProvider.DidNotReceive().GetDbContextAsync();
    }

    [Fact]
    public async Task AppendExplicit_InvalidNode_ThrowsFileReferenceInvalid_BeforeTouchingTheDatabase()
    {
        _offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<FileOffloadResult>.Fail(WorkflowErrors.FileReferenceInvalid("passport", "'content' is not valid base64")));
        var instance = InstanceFactory.CreateDefault();

        var ex = await Should.ThrowAsync<FileReferenceInvalidException>(() => CreateService().AppendExplicitAsync(
            instance, Guid.NewGuid(), "1.0.0", new JsonData(Delta), CancellationToken.None, CreateWorkflow()));

        ex.Message.ShouldBe("The file at \"passport\" is invalid: 'content' is not valid base64");
        await _dbContextProvider.DidNotReceive().GetDbContextAsync();
    }

    [Fact]
    public async Task FlowWithoutXStorage_NeverCallsTheOffload()
    {
        _offload.GetFields(_schema).Returns((IReadOnlyList<FileStorageField>)[]);
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();

        var row = await CreateService().AppendAsync(instance, new JsonData(Delta), VersionStrategy.IncreaseMinor,
            CancellationToken.None, CreateWorkflow());

        row.ShouldNotBeNull();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
    }

    [Fact]
    public async Task NoWorkflow_SkipsTheOffload_LikeTheSchemaHandling()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();

        await CreateService().AppendAsync(instance, new JsonData(Delta), VersionStrategy.IncreaseMinor);

        _offload.DidNotReceiveWithAnyArgs().GetFields(default!);
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
    }

    [Fact]
    public async Task XStorageFlow_DeltaWithoutContentOrFile_NoOffloadCall_AndNoSpan()
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        using var root = new Activity("funnel-files").Start();
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();

        var row = await CreateService().AppendAsync(instance,
            new JsonData("""{"passport":{"name":"p.pdf"},"other":{"file":"not-at-an-x-storage-path"}}"""),
            VersionStrategy.IncreaseMinor, CancellationToken.None, CreateWorkflow());

        row.ShouldNotBeNull();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        spans.ShouldNotContain(a => a.TraceId == root.TraceId && a.DisplayName == "Files.Offload");
    }

    [Fact]
    public async Task XStorageFlow_DeltaWithContent_OffloadsOnce_UnderOneSpan()
    {
        _offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<FileOffloadResult>.Ok(new FileOffloadResult(JsonDocument.Parse(Handled).RootElement.Clone(), true)));
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        using var root = new Activity("funnel-files").Start();
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();

        await CreateService().AppendAsync(instance, new JsonData(Delta), VersionStrategy.IncreaseMinor,
            CancellationToken.None, CreateWorkflow());

        await _offload.Received(1).OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>());
        spans.Count(a => a.TraceId == root.TraceId && a.DisplayName == "Files.Offload").ShouldBe(1);
    }

    /// <summary>A handle in a runtime-produced delta is validated by the Trusted offload; a forged one fails the write.</summary>
    [Fact]
    public async Task XStorageFlow_DeltaWithAForgedHandle_ThrowsFileReferenceInvalid_BeforeTouchingTheDatabase()
    {
        _offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<FileOffloadResult>.Fail(WorkflowErrors.FileReferenceInvalid("passport", "the file reference is not a valid handle of an allowed component")));
        var instance = InstanceFactory.CreateDefault();

        await Should.ThrowAsync<FileReferenceInvalidException>(() => CreateService().AppendAsync(
            instance, new JsonData(Handled), VersionStrategy.IncreaseMinor, CancellationToken.None, CreateWorkflow()));

        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => r.Mode == FileOffloadMode.Trusted), Arg.Any<CancellationToken>());
        await _dbContextProvider.DidNotReceive().GetDbContextAsync();
    }

    [Fact]
    public async Task SchemaUnavailable_DeltaWithAFileNode_ThrowsFileSchemaUnavailable_BeforeTouchingTheDatabase()
    {
        _componentCache.GetSchemaAsync("test-domain", "master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.Failure("schema:down", "down")));
        var instance = InstanceFactory.CreateDefault();

        var ex = await Should.ThrowAsync<FileSchemaUnavailableException>(() => CreateService().AppendAsync(
            instance, new JsonData(Delta), VersionStrategy.IncreaseMinor, CancellationToken.None, CreateWorkflow()));

        ex.Code.ShouldBe(WorkflowErrorCodes.FileSchemaUnavailable);
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        await _dbContextProvider.DidNotReceive().GetDbContextAsync();
    }

    [Fact]
    public async Task SchemaUnavailable_DeltaWithoutAFileNode_IsWrittenAsBefore()
    {
        _componentCache.GetSchemaAsync("test-domain", "master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.Failure("schema:down", "down")));
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();

        var row = await CreateService().AppendAsync(instance, new JsonData("""{"name":"x"}"""),
            VersionStrategy.IncreaseMinor, CancellationToken.None, CreateWorkflow());

        row.ShouldNotBeNull();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
    }

    private static ActivityListener Listen(List<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static SchemaDefinition CreateSchema()
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            """{ "type": "JSON", "schema": { "type": "object" } }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference("master", "test-domain", "sys-schemas", "1.0.0"));
        return schema;
    }

    private static BBT.Workflow.Definitions.Workflow CreateWorkflow()
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
        workflow.SetSchema(new Reference("master", "test-domain", "sys-schemas", "1.0.0"));
        return workflow;
    }
}
