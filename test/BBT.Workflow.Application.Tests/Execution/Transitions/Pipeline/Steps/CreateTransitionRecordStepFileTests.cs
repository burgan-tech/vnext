using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Files;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// x-storage defence in depth in the mapping step (spec §3): content a transition mapping produces is offloaded
/// (Trusted) before it reaches the data funnel or the transition record body.
/// </summary>
[Collection(BBT.Workflow.Application.Tests.TracingDetailLevelCollection.Name)]
public class CreateTransitionRecordStepFileTests
{
    private const string Mapped = """{"passport":{"content":"aGk=","name":"p.pdf"}}""";
    private const string Handled = """{"passport":{"component":"vnext-blob-local","file":"f-1","name":"p.pdf"}}""";

    private readonly IInstanceTransitionRepository _transitionRepository = Substitute.For<IInstanceTransitionRepository>();
    private readonly IInstanceDataWriteService _dataWriteService = Substitute.For<IInstanceDataWriteService>();
    private readonly ITransitionDataMapper _dataMapper = Substitute.For<ITransitionDataMapper>();
    private readonly IFileOffloadService _offload = Substitute.For<IFileOffloadService>();
    private readonly CreateTransitionRecordStep _step;

    public CreateTransitionRecordStepFileTests()
    {
        _dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(), Arg.Any<IRuntimeInfoProvider>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<object?>.Ok(JsonDocument.Parse(Mapped).RootElement.Clone()));
        _offload.GetFieldsAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FileStorageField>)[new FileStorageField(["passport"], "vnext-blob-local")]);

        _step = new CreateTransitionRecordStep(
            _transitionRepository,
            Substitute.For<IInstanceRepository>(),
            _dataWriteService,
            Substitute.For<IGuidGenerator>(),
            _dataMapper,
            Substitute.For<IRuntimeInfoProvider>(),
            _offload,
            Substitute.For<ILogger<CreateTransitionRecordStep>>());
    }

    [Fact]
    public async Task MappingOutputWithContent_IsOffloaded_BeforeTheAppendAndTheRecordBody()
    {
        _offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<FileOffloadResult>.Ok(new FileOffloadResult(JsonDocument.Parse(Handled).RootElement.Clone(), true)));
        var context = CreateContext(withMapping: true);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => r.Mode == FileOffloadMode.Trusted && r.InstanceId == context.Instance.Id
                                            && r.Payload!.Value.GetProperty("passport").GetProperty("content").GetString() == "aGk="),
            Arg.Any<CancellationToken>());
        await _dataWriteService.Received(1).AppendAsync(
            context.Instance,
            Arg.Is<JsonData>(d => !d.Json.Contains("content") && d.Json.Contains("f-1")),
            Arg.Any<VersionStrategy?>(), Arg.Any<CancellationToken>(), context.Workflow);
        var record = (InstanceTransition)context.Items[WellKnownItems.InstanceTransition]!;
        record.Body!.Json.ShouldNotContain("content");
        record.Body.Json.ShouldContain("f-1");
    }

    [Fact]
    public async Task StoreFailure_ThrowsFileStoreUnavailable_AndAppendsNothing()
    {
        _offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<FileOffloadResult>.Fail(WorkflowErrors.FileStoreUnavailable("vnext-blob-local")));

        var ex = await Should.ThrowAsync<FileStoreUnavailableException>(
            () => _step.ExecuteAsync(CreateContext(withMapping: true), CancellationToken.None));

        ex.Message.ShouldContain("vnext-blob-local");
        await _dataWriteService.DidNotReceiveWithAnyArgs().AppendAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task InvalidNode_ThrowsFileReferenceInvalid_WithThePath()
    {
        _offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<FileOffloadResult>.Fail(WorkflowErrors.FileReferenceInvalid("passport", "'content' is not valid base64")));

        var ex = await Should.ThrowAsync<FileReferenceInvalidException>(
            () => _step.ExecuteAsync(CreateContext(withMapping: true), CancellationToken.None));

        ex.Message.ShouldBe("The file at \"passport\" is invalid: 'content' is not valid base64");
    }

    [Fact]
    public async Task MappingOutputWithoutContent_NoOffloadCall_AndNoSpan()
    {
        _dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(), Arg.Any<IRuntimeInfoProvider>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<object?>.Ok(JsonDocument.Parse(Handled).RootElement.Clone()));
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        using var root = new Activity("mapping-no-content").Start();

        var result = await _step.ExecuteAsync(CreateContext(withMapping: true), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        spans.ShouldNotContain(a => a.TraceId == root.TraceId && a.DisplayName == "Files.Offload");
    }

    [Fact]
    public async Task MappingOutputWithContent_OffloadsOnce_UnderOneSpan_WithTheFields()
    {
        _offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<FileOffloadResult>.Ok(new FileOffloadResult(JsonDocument.Parse(Handled).RootElement.Clone(), true)));
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        using var root = new Activity("mapping-content").Start();

        await _step.ExecuteAsync(CreateContext(withMapping: true), CancellationToken.None);

        await _offload.Received(1).OffloadAsync(Arg.Is<FileOffloadRequest>(r => r.Fields != null), Arg.Any<CancellationToken>());
        spans.Count(a => a.TraceId == root.TraceId && a.DisplayName == "Files.Offload").ShouldBe(1);
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

    [Fact]
    public async Task NoMapping_LeavesTheSwapToAdmissionAndTheFunnel()
    {
        var result = await _step.ExecuteAsync(CreateContext(withMapping: false), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
    }

    [Fact]
    public async Task FlowWithoutXStorage_SkipsTheOffloadEntirely()
    {
        _offload.GetFieldsAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FileStorageField>)[]);

        var result = await _step.ExecuteAsync(CreateContext(withMapping: true), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        await _dataWriteService.Received(1).AppendAsync(
            Arg.Any<Instance>(), Arg.Is<JsonData>(d => d.Json.Contains("content")),
            Arg.Any<VersionStrategy?>(), Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>());
    }

    private static TransitionExecutionContext CreateContext(bool withMapping)
    {
        var workflow = CreateWorkflow();
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        var state = workflow.GetState("state1").Value!;
        instance.ChangeState(state);
        if (withMapping)
            workflow.ResolveTransition("test-transition", state)!.SetMapping(ScriptCode.FromNative("mapping"));

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = Transition.Create("test-transition", null, "state1", TriggerType.Manual, "Patch"),
            Instance = instance,
            Data = null,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow()
    {
        var json = """
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           {"key": "state1", "stateType": "Intermediate", "transitions": [
                               {"key": "test-transition", "from": "state1", "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": []}
                           ]}
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
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
