using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Tasks.Invocation.Local;
using Dapr;
using Dapr.Client;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// vnext-client-sdk-core#142-B/D: a DaprBinding task returns the binding's answer (get/list/delete) to its output
/// mapping, names the sidecar's reason on failure, and can run from the orchestration sidecar where the x-storage
/// components live.
/// </summary>
public sealed class DaprBindingInvocationTests
{
    private readonly DaprClient _dapr = Substitute.For<DaprClient>();

    private static DaprBindingTaskBinding Binding(string operation = "get", string? body = null, Dictionary<string, string>? metadata = null) => new()
    {
        BindingName = "vnext-blob-s3",
        Operation = operation,
        Body = body,
        Metadata = metadata ?? new Dictionary<string, string> { ["key"] = "rt/f1" },
    };

    private void Answers(byte[] data, Dictionary<string, string>? metadata = null)
        => _dapr.InvokeBindingAsync(Arg.Any<BindingRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new BindingResponse(ci.Arg<BindingRequest>(), data, metadata ?? new Dictionary<string, string>()));

    [Fact]
    public async Task JsonAnswer_IsTheTasksData_AndTheMetadataItsHeaders()
    {
        Answers(Encoding.UTF8.GetBytes("""{ "keys": ["rt/a", "rt/b"] }"""), new Dictionary<string, string> { ["marker"] = "m1" });

        var result = await DaprBindingInvocation.ExecuteAsync(_dapr, Binding("list"), "DaprBinding", default);

        result.IsSuccess.ShouldBeTrue();
        ((JsonElement)result.Data!).GetProperty("keys").GetArrayLength().ShouldBe(2);
        result.Headers!["marker"].ShouldBe("m1");
        result.Metadata!["DataEncoding"].ShouldBe("json");
        result.Metadata["Operation"].ShouldBe("list");
    }

    [Fact]
    public async Task BinaryAnswer_IsBase64()
    {
        Answers([0x25, 0x50, 0x44, 0x46, 0x00, 0xFF]);
        var result = await DaprBindingInvocation.ExecuteAsync(_dapr, Binding(), "DaprBinding", default);
        result.Data.ShouldBe(Convert.ToBase64String(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF }));
        result.Metadata!["DataEncoding"].ShouldBe("base64");
    }

    [Fact]
    public async Task TextAnswer_IsText_EmptyAnswer_IsNone()
    {
        Answers(Encoding.UTF8.GetBytes("deleted ok"));
        var text = await DaprBindingInvocation.ExecuteAsync(_dapr, Binding("delete"), "DaprBinding", default);
        text.Data.ShouldBe("deleted ok");
        text.Metadata!["DataEncoding"].ShouldBe("text");

        Answers([]);
        var none = await DaprBindingInvocation.ExecuteAsync(_dapr, Binding("delete"), "DaprBinding", default);
        none.IsSuccess.ShouldBeTrue();
        none.Data.ShouldBeNull();
        none.Metadata!["DataEncoding"].ShouldBe("none");
    }

    [Fact]
    public async Task Request_UsesTheMethodOverride_DropsInternalKeys_AndSendsTheBodyBytes()
    {
        BindingRequest? sent = null;
        _dapr.InvokeBindingAsync(Arg.Do<BindingRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(ci => new BindingResponse(ci.Arg<BindingRequest>(), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>()));

        await DaprBindingInvocation.ExecuteAsync(_dapr, Binding("create", """{"a":1}""",
            new Dictionary<string, string> { ["method"] = "delete", ["ForwardingHeaders"] = "x", ["key"] = "rt/f1" }), "DaprBinding", default);

        sent!.Operation.ShouldBe("delete");
        sent.Metadata.ShouldContainKey("key");
        sent.Metadata.ShouldNotContainKey("method");
        sent.Metadata.ShouldNotContainKey("ForwardingHeaders");
        Encoding.UTF8.GetString(sent.Data.Span).ShouldBe("""{"a":1}""");
    }

    [Fact]
    public async Task SidecarFailure_NamesTheSidecarsReason_NotTheGenericClientMessage()
    {
        var rpc = new RpcException(new Status(StatusCode.Internal, "couldn't find output binding vnext-blob-s3"));
        _dapr.InvokeBindingAsync(Arg.Any<BindingRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DaprException("Binding operation failed: the Dapr endpoint indicated a failure.", rpc));

        var result = await DaprBindingInvocation.ExecuteAsync(_dapr, Binding("delete"), "DaprBinding", default);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("couldn't find output binding vnext-blob-s3");
        result.Metadata!["ExceptionType"].ShouldBe(nameof(DaprException));
    }

    [Fact]
    public async Task LocalInvoker_RunsTheSameCore_AndMapsToTheOrchestratorResult()
    {
        Answers(Encoding.UTF8.GetBytes("""{ "ok": true }"""));
        var invoker = new LocalDaprBindingTaskInvoker(_dapr, NullLogger<LocalDaprBindingTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("delete-file", JsonSerializer.SerializeToElement(Binding("delete")), null);

        invoker.TaskType.ShouldBe(BBT.Workflow.Execution.TaskTypes.DaprBinding);
        result.IsSuccess.ShouldBeTrue();
        ((JsonElement)result.Data!).GetProperty("ok").GetBoolean().ShouldBeTrue();
    }
}
