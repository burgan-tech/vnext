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
        var stored = Json("""{ "files": [ { "component": "vnext-blob-local", "file": "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80", "name": "a.pdf", "mimeType": "application/pdf", "size": 3, "eTag": "e", "owner": { "domain": "core", "flow": "kyc", "instance": "i" } } ] }""");
        var payload = Json("""{ "files": [ { "file": "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80", "mimeType": "text/html" } ] }""");

        var r = (await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, stored, FileOffloadMode.External), default)).Value!;

        r.Payload!.Value.GetProperty("files")[0].GetProperty("mimeType").GetString().ShouldBe("application/pdf");
        await _store.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default, default, default);
    }

    [Theory]
    [InlineData("""{ "files": [ { "file": "other" } ] }""")]   // not stored on this instance
    [InlineData("""{ "passport": { "file": "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80" } }""")]       // stored, but under another path
    public async Task External_UnknownReference_Is400(string payload)
    {
        var stored = Json("""{ "files": [ { "component": "vnext-blob-local", "file": "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80", "size": 1, "eTag": "e", "owner": { "domain": "d", "flow": "f", "instance": "i" } } ] }""");
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json(payload), stored, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task External_ReferenceOnStart_Is400()
    {
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "passport": { "file": "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80" } }"""), null, FileOffloadMode.External), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task Trusted_ReferenceIsKeptAsIs()
    {
        var payload = Json("""{ "passport": { "component": "vnext-blob-local", "file": "5d2c1e8a-77b4-4c0e-8f1a-9e6b3c2d1a00", "size": 1, "eTag": "e", "owner": { "domain": "d", "flow": "p", "instance": "i" } } }""");
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
        var stored = Json("""{ "files": [ { "component": "vnext-blob-local", "file": "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80", "size": 1, "eTag": "e", "owner": { "domain": "d", "flow": "f", "instance": "i" } } ] }""");
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

    // ── I1: a handle is valid only with a GUID file id and an allowed component ───────────────────────────────

    private const string ValidFile = "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80";

    private static string HandleJson(string component, string file, string eTag = "e", string ownerInstance = "i") =>
        $$"""{ "component": "{{component}}", "file": "{{file}}", "size": 1, "eTag": "{{eTag}}", "owner": { "domain": "d", "flow": "f", "instance": "{{ownerInstance}}" } }""";

    [Theory]
    [InlineData("vnext-blob-local", "../../etc/passwd")]           // not a GUID: never an object key
    [InlineData("vnext-blob-local", "not-a-guid")]
    [InlineData("vnext-blob-local", "0b9f6f3e1c1a4a7e9c553f1d2a6b7c80")] // GUID, but not "D" format
    [InlineData("some-other-binding", ValidFile)]                  // component neither declared nor allowed
    public async Task Trusted_ForgedHandle_Is400(string component, string file)
    {
        var payload = Json($$"""{ "passport": {{HandleJson(component, file)}} }""");
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, null, FileOffloadMode.Trusted), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Theory]
    [InlineData("""{ "file": "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80" }""")]                             // partial node: not a handle
    [InlineData("""{ "component": "vnext-blob-local", "file": "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80", "size": 1, "eTag": " ", "owner": { "domain": "d", "flow": "f", "instance": "i" } }""")]
    [InlineData("""{ "component": "vnext-blob-local", "file": "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80", "size": 1, "eTag": "e", "owner": { "domain": "", "flow": "f", "instance": "i" } }""")]
    public async Task Trusted_IncompleteHandle_Is400(string node)
    {
        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json($$"""{ "passport": {{node}} }"""), null, FileOffloadMode.Trusted), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task Trusted_HandleOfAnAllowedBinding_IsKept()
    {
        var (sut, workflow) = FileOffloadTestFactory.Create(Master, _store, "core", "kyc",
            new FileStorageOptions { AllowedBindings = ["vnext-blob-parent"] });
        var payload = Json($$"""{ "passport": {{HandleJson("vnext-blob-parent", ValidFile)}} }""");

        var r = await sut.OffloadAsync(new(workflow, Guid.NewGuid(), payload, null, FileOffloadMode.Trusted), default);

        r.IsSuccess.ShouldBeTrue();
        r.Value!.Changed.ShouldBeFalse();
    }

    [Fact]
    public async Task External_EchoOfAnInvalidStoredHandle_Is400()
    {
        // A stored record naming a foreign component (written before validation existed, or forged) is not echoed.
        var stored = Json($$"""{ "passport": {{HandleJson("some-other-binding", ValidFile)}} }""");
        var payload = Json($$"""{ "passport": { "file": "{{ValidFile}}" } }""");

        var r = await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, stored, FileOffloadMode.External), default);

        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    // ── I3: master schema unavailable ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SchemaUnavailable_GetFieldsFails_AndOffloadRefusesAFilePayload()
    {
        var cacheFailing = Substitute.For<BBT.Workflow.Caching.IComponentCacheStore>();
        cacheFailing.GetSchemaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.Failure("schema:down", "down")));
        var sut = new FileOffloadService(cacheFailing, _store, Substitute.For<BBT.Workflow.Runtime.IRuntimeInfoProvider>(),
            Microsoft.Extensions.Options.Options.Create(new FileStorageOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileOffloadService>.Instance);

        var fields = await sut.GetFieldsAsync(_workflow, default);
        fields.IsSuccess.ShouldBeFalse();
        fields.Error.Code.ShouldBe(WorkflowErrorCodes.FileSchemaUnavailable);

        var withFile = await sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "passport": { "content": "AA==" } }"""), null, FileOffloadMode.Trusted), default);
        withFile.Error.Code.ShouldBe(WorkflowErrorCodes.FileSchemaUnavailable);

        var withoutFile = await sut.OffloadAsync(new(_workflow, Guid.NewGuid(), Json("""{ "a": { "b": [1, 2] } }"""), null, FileOffloadMode.External), default);
        withoutFile.IsSuccess.ShouldBeTrue();
        withoutFile.Value!.Changed.ShouldBeFalse();
        await _store.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task NoMasterSchema_HasNoFields()
    {
        var workflow = Definitions.Workflow.Create();
        var fields = await _sut.GetFieldsAsync(workflow, default);
        fields.IsSuccess.ShouldBeTrue();
        fields.Value!.ShouldBeEmpty();
    }

    // ── I2: stored name and media type ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("application/pdf", "application/pdf")]
    [InlineData("  Text/HTML ; charset=utf-8", "Text/HTML; charset=utf-8")]
    [InlineData("not a media type", null)]
    [InlineData("text/html\\r\\nX-Injected: 1", null)] // JSON-escaped CR LF: decoded, then rejected by the parser
    [InlineData("", null)]
    public async Task MimeType_IsNormalisedOrDropped(string declared, string? stored)
    {
        var payload = Json($$"""{ "passport": { "mimeType": "{{declared}}", "content": "AA==" } }""");

        var r = (await _sut.OffloadAsync(new(_workflow, Guid.NewGuid(), payload, null, FileOffloadMode.External), default)).Value!;

        var mime = r.Payload!.Value.GetProperty("passport").GetProperty("mimeType");
        (mime.ValueKind == JsonValueKind.Null ? null : mime.GetString()).ShouldBe(stored);
    }

    [Fact]
    public void Name_IsCappedAt255_KeepingAShortExtension()
    {
        var name = new string('a', 400) + ".pdf";
        var capped = FileOffloadService.NormalizeName(name)!;
        capped.Length.ShouldBe(255);
        capped.ShouldEndWith(".pdf");

        FileOffloadService.NormalizeName(new string('b', 300))!.Length.ShouldBe(255);
        FileOffloadService.NormalizeName("ok.pdf").ShouldBe("ok.pdf");
        FileOffloadService.NormalizeName("a\r\nb.pdf").ShouldBe("a__b.pdf");
        FileOffloadService.NormalizeName(null).ShouldBeNull();
    }

    [Fact]
    public async Task Fields_AreMemoizedByContent()
    {
        var (a, wa) = FileOffloadTestFactory.Create(Master, _store, "core", "kyc");
        var (b, wb) = FileOffloadTestFactory.Create(Master, _store, "core", "kyc");
        var other = Master.Replace("vnext-blob-local", "vnext-blob-other");
        var (c, wc) = FileOffloadTestFactory.Create(other, _store, "core", "kyc");

        var fa = (await a.GetFieldsAsync(wa, default)).Value;
        var fb = (await b.GetFieldsAsync(wb, default)).Value;
        var fc = (await c.GetFieldsAsync(wc, default)).Value;

        fb.ShouldBeSameAs(fa);
        fc.ShouldNotBeSameAs(fa);
    }
}
