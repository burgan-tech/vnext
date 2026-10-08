using System;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Logging;
using BBT.Workflow.Files;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Files;

public sealed class FileOffloadServiceTests
{
    private const string Master = """
        { "type": "object", "properties": {
            "passport": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } },
            "files": { "type": "array", "items": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } } } } }
        """;

    private readonly IFileBlobStore _store = Substitute.For<IFileBlobStore>();
    private readonly FileOffloadService _sut;
    private readonly Definitions.Workflow _workflow;

    public FileOffloadServiceTests()
    {
        _store.PutAsync(default!, default!, default, default, default).ReturnsForAnyArgs(Result.Ok());
        (_sut, _workflow) = FileOffloadTestFactory.Create(Master, _store, domain: "core", flow: "kyc");
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task Content_IsStoredAndReplacedByHandle()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var payload = Json($$"""{ "passport": { "name": "p.pdf", "mimeType": "application/pdf", "size": 999, "content": "{{Convert.ToBase64String(bytes)}}" }, "other": 1 }""");
        var id = Guid.NewGuid();

        var result = (await _sut.OffloadAsync(new(_workflow, id, payload, null, FileOffloadMode.External), default)).Value!;

        result.Changed.ShouldBeTrue();
        var p = result.Payload!.Value.GetProperty("passport");
        p.TryGetProperty("content", out _).ShouldBeFalse();
        p.GetProperty("component").GetString().ShouldBe("vnext-blob-local");
        p.GetProperty("size").GetInt64().ShouldBe(3);
        p.GetProperty("eTag").GetString().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        p.GetProperty("owner").GetProperty("instance").GetString().ShouldBe(id.ToString());
        p.GetProperty("owner").GetProperty("domain").GetString().ShouldBe("core");
        p.GetProperty("owner").GetProperty("flow").GetString().ShouldBe("kyc");
        result.Payload!.Value.GetProperty("other").GetInt32().ShouldBe(1);
        await _store.Received(1).PutAsync("vnext-blob-local", p.GetProperty("file").GetString()!, Arg.Any<ReadOnlyMemory<byte>>(), "application/pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestFields_AreUsedInsteadOfReDerivingThem()
    {
        var payload = Json("""{ "passport": { "content": "AA==" } }""");

        var r = (await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, null, FileOffloadMode.Trusted, Fields: []), default)).Value!;

        r.Changed.ShouldBeFalse();
        await _store.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default, default, default);
    }

    [Fact]
    public void GetFields_MemoizesByContent_NotByInstanceOrReference()
    {
        static SchemaDefinition Schema(string json)
        {
            var s = JsonSerializer.Deserialize<SchemaDefinition>($$"""{ "type": "JSON", "schema": {{json}} }""",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            s.SetReference(new Reference("master", "core", "sys-schemas", "1.0.0"));
            return s;
        }

        var first = _sut.GetFields(Schema(Master));
        var again = _sut.GetFields(Schema(Master));
        // Same reference tuple, republished content: must not be served from the old entry.
        var republished = _sut.GetFields(Schema("""{ "type": "object", "properties": { "selfie": { "type": "object", "x-storage": { "binding": "b2" } } } }"""));

        first.Count.ShouldBe(2);
        again.ShouldBeSameAs(first);
        republished.ShouldHaveSingleItem().Binding.ShouldBe("b2");
    }

    [Fact]
    public async Task ContentAndFile_Together_Is400()
    {
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "passport": { "file": "x", "content": "AA==" } }"""), null, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task InvalidBase64_Is400()
    {
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "passport": { "content": "%%%" } }"""), null, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task External_EchoOfStoredFile_IsReplacedByStoredHandle()
    {
        var stored = Json("""{ "files": [ { "component": "vnext-blob-local", "file": "f1", "name": "a.pdf", "mimeType": "application/pdf", "size": 3, "eTag": "e", "owner": { "domain": "core", "flow": "kyc", "instance": "i" } } ] }""");
        var payload = Json("""{ "files": [ { "file": "f1", "mimeType": "text/html" } ] }""");

        var r = (await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, stored, FileOffloadMode.External), default)).Value!;

        r.Payload!.Value.GetProperty("files")[0].GetProperty("mimeType").GetString().ShouldBe("application/pdf");
        await _store.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default, default, default);
    }

    [Theory]
    [InlineData("""{ "files": [ { "file": "other" } ] }""")]   // not stored on this instance
    [InlineData("""{ "passport": { "file": "f1" } }""")]       // stored, but under another path
    public async Task External_UnknownReference_Is400(string payload)
    {
        var stored = Json("""{ "files": [ { "component": "vnext-blob-local", "file": "f1", "size": 1, "eTag": "e", "owner": { "domain": "d", "flow": "f", "instance": "i" } } ] }""");
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json(payload), stored, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task External_ReferenceOnStart_Is400()
    {
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "passport": { "file": "f1" } }"""), null, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task Trusted_ReferenceIsKeptAsIs()
    {
        var payload = Json("""{ "passport": { "component": "vnext-blob-local", "file": "parent-file", "size": 1, "eTag": "e", "owner": { "domain": "d", "flow": "p", "instance": "i" } } }""");
        var r = (await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, null, FileOffloadMode.Trusted), default)).Value!;
        r.Changed.ShouldBeFalse();
    }

    [Fact]
    public async Task StoreFailure_Is503_AndNothingElseIsStored()
    {
        _store.PutAsync(default!, default!, default, default, default).ReturnsForAnyArgs(Result.Fail(WorkflowErrors.FileStoreUnavailable("vnext-blob-local")));
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "passport": { "content": "AA==" } }"""), null, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
    }

    [Fact]
    public async Task NoXStorage_OrNoPayload_IsUnchanged()
    {
        (await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), null, null, FileOffloadMode.External), default)).Value!.Changed.ShouldBeFalse();
        (await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "a": 1 }"""), null, FileOffloadMode.External), default)).Value!.Changed.ShouldBeFalse();
    }

    [Fact]
    public async Task External_EscapedFilePropertyName_IsNotABypass()
    {
        var payload = Json("{ \"passport\": { \"\\u0066ile\": \"forged\" } }");
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, null, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task Trusted_ContentIsStillOffloaded()
    {
        var r = (await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "passport": { "content": "AA==" } }"""), null, FileOffloadMode.Trusted), default)).Value!;
        r.Changed.ShouldBeTrue();
        await _store.Received(1).PutAsync("vnext-blob-local", Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Is<string?>(m => m == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonStringContent_Is400()
    {
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "passport": { "content": 5 } }"""), null, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task RejectedRequest_WritesNothing()
    {
        var payload = Json("""{ "passport": { "content": "AA==" }, "files": [ { "file": "nope" } ] }""");
        var stored = Json("""{ "files": [ { "component": "vnext-blob-local", "file": "f1", "size": 1, "eTag": "e", "owner": { "domain": "d", "flow": "f", "instance": "i" } } ] }""");
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, stored, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
        await _store.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task SecondPutFails_ReturnsStoreUnavailable()
    {
        _store.PutAsync(default!, default!, default, default, default).ReturnsForAnyArgs(
            Result.Ok(), Result.Fail(WorkflowErrors.FileStoreUnavailable("vnext-blob-local")));
        var payload = Json("""{ "passport": { "content": "AA==" }, "files": [ { "content": "AQ==" } ] }""");
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, null, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
    }

    [Fact]
    public async Task Fields_AreMemoizedByContent()
    {
        var (a, wa) = FileOffloadTestFactory.Create(Master, _store, "core", "kyc");
        var (b, wb) = FileOffloadTestFactory.Create(Master, _store, "core", "kyc");
        var other = Master.Replace("vnext-blob-local", "vnext-blob-other");
        var (c, wc) = FileOffloadTestFactory.Create(other, _store, "core", "kyc");

        var fa = await a.GetFieldsAsync(wa, default);
        var fb = await b.GetFieldsAsync(wb, default);
        var fc = await c.GetFieldsAsync(wc, default);

        fb.ShouldBeSameAs(fa);
        fc.ShouldNotBeSameAs(fa);
    }
}
