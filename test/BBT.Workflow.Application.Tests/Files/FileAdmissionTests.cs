using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Transitions;
using BBT.Workflow.Files;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Files;

/// <summary>
/// Pins the x-storage swap at transition admission (spec §3): the swapped payload replaces the request
/// data on both contexts and the raw body, a store/reference failure leaves the request untouched, a
/// request the parent relays to its active SubFlow is left for the leaf, and the trust mode and the
/// instance's LatestData reach the offloader.
/// </summary>
[Collection(BBT.Workflow.Application.Tests.TracingDetailLevelCollection.Name)]
public sealed class FileAdmissionTests
{
    private static readonly JsonElement Incoming = JsonDocument.Parse(
        """{ "passport": { "name": "p.pdf", "mimeType": "application/pdf", "content": "AAEC" } }""").RootElement.Clone();

    private static readonly JsonElement Swapped = JsonDocument.Parse(
        """{ "passport": { "component": "vnext-blob-local", "file": "6f1c", "name": "p.pdf", "mimeType": "application/pdf", "size": 3, "eTag": "ab" } }""").RootElement.Clone();

    private readonly IFileOffloadService _offload = Substitute.For<IFileOffloadService>();
    private readonly IRequestRawBodyProvider _rawBody = Substitute.For<IRequestRawBodyProvider>();
    private readonly FileAdmission _sut;

    public FileAdmissionTests()
    {
        _offload.OffloadAsync(default!, default).ReturnsForAnyArgs(
            Result<FileOffloadResult>.Ok(new FileOffloadResult(Swapped, Changed: true)));
        _offload.GetFieldsAsync(default!, default).ReturnsForAnyArgs(
            Result<IReadOnlyList<FileStorageField>>.Ok([new FileStorageField(["passport"], "vnext-blob-local")]));
        _sut = new FileAdmission(_offload, _rawBody);
    }

    [Fact]
    public async Task ApplyAsync_WithContent_SwapsDataOnBothContextsAndTheRawBody()
    {
        var (ctx, wf) = Create();

        var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        ctx.DataElement!.Value.GetRawText().ShouldBe(Swapped.GetRawText());
        wf.Data!.Attributes!.Value.GetRawText().ShouldBe(Swapped.GetRawText());
        _rawBody.Received(1).ReplaceRawBodyAttributes(Arg.Is<JsonElement?>(e => e.HasValue && e.Value.GetRawText() == Swapped.GetRawText()));
    }

    [Fact]
    public async Task ApplyAsync_Unchanged_LeavesTheRequestAndRawBodyAlone()
    {
        _offload.OffloadAsync(default!, default).ReturnsForAnyArgs(
            Result<FileOffloadResult>.Ok(new FileOffloadResult(Incoming, Changed: false)));
        var (ctx, wf) = Create();

        var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        ctx.DataElement!.Value.GetRawText().ShouldBe(Incoming.GetRawText());
        _rawBody.DidNotReceiveWithAnyArgs().ReplaceRawBodyAttributes(default);
    }

    [Fact]
    public async Task ApplyAsync_OffloadFails_ReturnsTheErrorAndTouchesNothing()
    {
        var error = WorkflowErrors.FileStoreUnavailable("vnext-blob-local");
        _offload.OffloadAsync(default!, default).ReturnsForAnyArgs(Result<FileOffloadResult>.Fail(error));
        var (ctx, wf) = Create();

        var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
        ctx.DataElement!.Value.GetRawText().ShouldBe(Incoming.GetRawText());
        wf.Data!.Attributes!.Value.GetRawText().ShouldBe(Incoming.GetRawText());
        _rawBody.DidNotReceiveWithAnyArgs().ReplaceRawBodyAttributes(default);
    }

    [Fact]
    public async Task ApplyAsync_WhenTheParentWillForward_LeavesTheSwapToTheLeaf()
    {
        var (ctx, wf) = Create(withActiveSubFlow: true);
        SubflowForwardRule.WillForward(ctx).ShouldBeTrue();

        var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        ctx.DataElement!.Value.GetRawText().ShouldBe(Incoming.GetRawText());
    }

    [Theory]
    [InlineData("waiting-child", false)] // available in the parent's current state: runs (and swaps) here
    [InlineData("some-other-state", true)] // not available: forwarded like any other key, the leaf swaps
    public async Task ApplyAsync_ParentSharedTransition_FollowsTheProxyPredicate(string availableIn, bool forwarded)
    {
        var (ctx, wf) = Create(withActiveSubFlow: true, transitionKey: "escalate");
        var shared = Transition.Create("escalate", null, "$self", TriggerType.Manual, "Patch");
        shared.AddAvailableIn(availableIn);
        ctx.Workflow.AddSharedTransition(shared);
        SubflowForwardRule.WillForward(ctx).ShouldBe(forwarded);

        await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        if (forwarded)
            await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        else
            await _offload.ReceivedWithAnyArgs(1).OffloadAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_UpdateDataOnAParentWithActiveSubFlow_IsSwappedHere()
    {
        // updateData is never forwarded (it writes the parent's own data), so the parent swaps it.
        var (ctx, wf) = Create(withActiveSubFlow: true, transitionKey: WellKnownTransitionKeys.UpdateData);
        SubflowForwardRule.WillForward(ctx).ShouldBeFalse();

        await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        await _offload.ReceivedWithAnyArgs(1).OffloadAsync(default!, default);
    }

    /// <summary>
    /// cancel and exit on a parent with an active SubFlow are not forwarded: the preflight step short-circuits them to
    /// the parent's own CreateTransition (past the forward order), so the parent records them and must swap them.
    /// </summary>
    [Theory]
    [InlineData(WellKnownTransitionKeys.Cancel)]
    [InlineData(WellKnownTransitionKeys.Exit)]
    public async Task ApplyAsync_CancelOrExitOnAParentWithActiveSubFlow_IsSwappedHere(string transitionKey)
    {
        var (ctx, wf) = Create(withActiveSubFlow: true, transitionKey: transitionKey);
        SubflowForwardRule.WillForward(ctx).ShouldBeFalse();

        var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => r.Mode == FileOffloadMode.External), Arg.Any<CancellationToken>());
        ctx.DataElement!.Value.GetRawText().ShouldBe(Swapped.GetRawText());
    }

    /// <summary>Same rows through the real offloader: a forged reference on cancel/exit is a 400, nothing stored.</summary>
    [Theory]
    [InlineData(WellKnownTransitionKeys.Cancel)]
    [InlineData(WellKnownTransitionKeys.Exit)]
    public async Task ApplyAsync_CancelOrExitOnAParentWithActiveSubFlow_RejectsAForgedReference(string transitionKey)
    {
        var store = Substitute.For<IFileBlobStore>();
        var (service, workflow) = FileOffloadTestFactory.Create(
            """{ "type": "object", "properties": { "passport": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } } } }""",
            store, domain: "test-domain", flow: "parent-workflow");
        var sut = new FileAdmission(service, _rawBody);
        var forged = JsonDocument.Parse(
            $$"""{ "passport": { "component": "vnext-blob-local", "file": "{{Guid.NewGuid()}}", "size": 1, "eTag": "e", "owner": { "domain": "d", "flow": "f", "instance": "i" } } }""").RootElement.Clone();
        var (ctx, wf) = Create(withActiveSubFlow: true, transitionKey: transitionKey, workflow: workflow, data: forged);

        var result = await sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
        await store.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default, default, default);
        _rawBody.DidNotReceiveWithAnyArgs().ReplaceRawBodyAttributes(default);
    }

    /// <summary>The fields admission already resolved reach the offload (no second derivation).</summary>
    [Fact]
    public async Task ApplyAsync_PassesTheResolvedFieldsToTheOffload()
    {
        var (ctx, wf) = Create();

        await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        await _offload.Received(1).GetFieldsAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<CancellationToken>());
        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => r.Fields != null && r.Fields.Count == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_SchemaUnavailable_WithAFileNode_Is503AndTouchesNothing()
    {
        _offload.GetFieldsAsync(default!, default).ReturnsForAnyArgs(
            Result<IReadOnlyList<FileStorageField>>.Fail(WorkflowErrors.FileSchemaUnavailable("master")));
        var (ctx, wf) = Create();

        var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileSchemaUnavailable);
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        ctx.DataElement!.Value.GetRawText().ShouldBe(Incoming.GetRawText());
    }

    [Fact]
    public async Task ApplyAsync_SchemaUnavailable_WithoutAFileNode_Proceeds()
    {
        _offload.GetFieldsAsync(default!, default).ReturnsForAnyArgs(
            Result<IReadOnlyList<FileStorageField>>.Fail(WorkflowErrors.FileSchemaUnavailable("master")));
        var (ctx, wf) = Create(data: JsonDocument.Parse("""{ "name": "x", "items": [ { "a": 1 } ] }""").RootElement.Clone());

        var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
    }

    [Theory]
    [InlineData(false, FileOffloadMode.External)]
    [InlineData(true, FileOffloadMode.Trusted)]
    public async Task ApplyAsync_PassesTheTrustModeOfTheRequest(bool trusted, FileOffloadMode expected)
    {
        var (ctx, wf) = Create();
        wf.TrustedPayload = trusted;

        await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => r.Mode == expected && r.InstanceId == ctx.Instance.Id
                                            && ReferenceEquals(r.Workflow, ctx.Workflow)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A trusted (runtime-produced) hop must not overwrite the outer request's raw body.</summary>
    [Fact]
    public async Task ApplyAsync_TrustedPayload_LeavesTheRawBodyAlone()
    {
        var (ctx, wf) = Create();
        wf.TrustedPayload = true;

        var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        ctx.DataElement!.Value.GetRawText().ShouldBe(Swapped.GetRawText());
        _rawBody.DidNotReceiveWithAnyArgs().ReplaceRawBodyAttributes(default);
    }

    [Fact]
    public async Task ApplyAsync_PassesTheInstanceLatestDataForTheEchoCheck()
    {
        var (ctx, wf) = Create();
        SeedLatestData(ctx.Instance, """{ "passport": { "file": "6f1c" } }""");

        await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => r.LatestData.HasValue
                                            && r.LatestData.Value.GetProperty("passport").GetProperty("file").GetString() == "6f1c"
                                            && r.Payload.HasValue
                                            && r.Payload.Value.GetRawText() == Incoming.GetRawText()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_NoLatestData_PassesNull()
    {
        var (ctx, wf) = Create();

        await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        await _offload.Received(1).OffloadAsync(
            Arg.Is<FileOffloadRequest>(r => !r.LatestData.HasValue), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A flow whose master schema declares no x-storage field does no file work and leaves no trace:
    /// no offload call, no <c>Files.Offload</c> span (trace-span-tree: non-applicable steps leave none).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_FlowWithoutXStorage_SkipsTheOffloadAndOpensNoSpan()
    {
        _offload.GetFieldsAsync(default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<FileStorageField>>.Ok([]));
        var key = "no-files-" + Guid.NewGuid().ToString("N");
        var (ctx, wf) = Create(transitionKey: key);
        var spans = ListenForFileSpans(key, out var listener);

        using (listener)
        {
            var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);
            result.IsSuccess.ShouldBeTrue();
        }

        spans.ShouldBeEmpty();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        _rawBody.DidNotReceiveWithAnyArgs().ReplaceRawBodyAttributes(default);
    }

    [Fact]
    public async Task ApplyAsync_NonObjectPayload_SkipsTheOffload()
    {
        var (ctx, wf) = Create();
        ctx.Data = null;

        var result = await _sut.ApplyAsync(ctx, wf, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _offload.DidNotReceiveWithAnyArgs().OffloadAsync(default!, default);
        await _offload.DidNotReceiveWithAnyArgs().GetFieldsAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_FlowWithXStorage_OpensTheFilesOffloadSpan()
    {
        var key = "with-files-" + Guid.NewGuid().ToString("N");
        var (ctx, wf) = Create(transitionKey: key);
        var spans = ListenForFileSpans(key, out var listener);

        using (listener)
        {
            await _sut.ApplyAsync(ctx, wf, CancellationToken.None);
        }

        spans.Count.ShouldBe(1);
    }

    /// <summary>Collects stopped <c>Files.Offload</c> spans tagged with this test's own transition key.</summary>
    private static List<Activity> ListenForFileSpans(string transitionKey, out ActivityListener listener)
    {
        var spans = new List<Activity>();
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.OperationName == "Files.Offload" && Equals(a.GetTagItem("vnext.transition.key"), transitionKey))
                    lock (spans) spans.Add(a);
            }
        };
        ActivitySource.AddActivityListener(listener);
        return spans;
    }

    private static (TransitionExecutionContext Ctx, WorkflowExecutionContext Wf) Create(
        bool withActiveSubFlow = false, string transitionKey = "submit",
        Definitions.Workflow? workflow = null, JsonElement? data = null)
    {
        var payload = data ?? Incoming;
        var instance = Instance.Create(Guid.NewGuid(), "parent-workflow", "1.0.0");
        if (withActiveSubFlow)
        {
            instance.AddCorrelation(InstanceCorrelation.Create(
                Guid.NewGuid(), instance.Id, "waiting-child", Guid.NewGuid(),
                SubFlowType.SubFlow.Code, "child-domain", "child-workflow", "1.0.0"));
        }

        var ctx = new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = instance.Flow,
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow ?? Definitions.Workflow.Create(),
            Current = StateFactory.CreateDefault("waiting-child", StateType.SubFlow),
            Transition = Transition.Create(transitionKey, "waiting-child", "waiting-child", TriggerType.Manual, "Patch"),
            Instance = instance,
            Data = payload,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
        var wf = new WorkflowExecutionContext
        {
            InstanceId = instance.Id.ToString(),
            Domain = "test-domain",
            WorkflowKey = instance.Flow,
            TransitionKey = transitionKey,
            Data = new TransitionDataInfo(payload)
        };
        return (ctx, wf);
    }

    /// <summary>The aggregate's persisted-row hook is internal to Domain; same reflection the benchmarks use.</summary>
    private static void SeedLatestData(Instance instance, string json)
    {
        var ctor = typeof(InstanceData).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Guid), typeof(Guid), typeof(string), typeof(JsonData), typeof(bool)])!;
        var row = (InstanceData)ctor.Invoke([Guid.NewGuid(), instance.Id, "1.0.0", new JsonData(json), true]);
        typeof(Instance).GetMethod("AcceptPersistedData", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, [row]);
    }
}
