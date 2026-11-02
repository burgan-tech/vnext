using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using BBT.Aether.Clock;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Files;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Files;

/// <summary>
/// Definition-based streaming (x-storage.transport): the upload endpoint stores and stages a file, a start or
/// transition claims it once by reference, and each field only accepts the channels its transport allows.
/// </summary>
public sealed class FileStreamedUploadTests
{
    private const string Master = """
        { "type": "object", "properties": {
            "inlineOnly": { "type": "object", "x-storage": { "binding": "store-a" } },
            "streamOnly": { "type": "object", "x-storage": { "binding": "store-a", "transport": "stream" } },
            "files": { "type": "array", "items": { "type": "object", "x-storage": { "binding": "store-a", "transport": "any" } } } } }
        """;

    private readonly IFileBlobStore _store = Substitute.For<IFileBlobStore>();
    private readonly InMemoryStaging _staging = new();
    private readonly FileOffloadService _sut;
    private readonly Definitions.Workflow _workflow;

    public FileStreamedUploadTests()
    {
        _store.PutAsync(default!, default!, default, default, default).ReturnsForAnyArgs(Result.Ok());
        (_sut, _workflow) = FileOffloadTestFactory.Create(Master, _store, domain: "core", flow: "kyc", staging: _staging);
    }

    private sealed class InMemoryStaging : IFileStagingStore
    {
        public readonly Dictionary<string, StagedFile> Entries = new();
        public Task SaveAsync(StagedFile staged, TimeSpan ttl, CancellationToken ct) { Entries[staged.File] = staged; return Task.CompletedTask; }
        public Task<StagedFile?> GetAsync(string domain, string file, CancellationToken ct)
            => Task.FromResult(Entries.TryGetValue(file, out var s) && s.Domain == domain ? s : null);
        public Task<StagedFile?> TakeAsync(string domain, string file, CancellationToken ct)
            => Task.FromResult(Entries.Remove(file, out var s) && s.Domain == domain ? s : null);
        public Task RestoreAsync(StagedFile staged, CancellationToken ct) { Entries[staged.File] = staged; return Task.CompletedTask; }
    }

    private StagedFile Stage(string path, string flow = "kyc", string component = "store-a")
    {
        var staged = new StagedFile
        {
            Domain = "core", Flow = flow, Path = path, Component = component, File = Guid.NewGuid().ToString(),
            Name = "scan.tiff", MimeType = "image/tiff", Size = 42, ETag = "e42", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        _staging.Entries[staged.File] = staged;
        return staged;
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private Task<Result<FileOffloadResult>> Offload(string payload, Guid? id = null, FileOffloadMode mode = FileOffloadMode.External)
        => _sut.OffloadAsync(new(_workflow, id ?? Guid.NewGuid(), Json(payload), null, mode), default);

    [Fact]
    public async Task StagedUpload_IsClaimed_AsTheInstancesHandle_KeepingClientMembers_AndIsSingleUse()
    {
        var staged = Stage("files[]");
        var id = Guid.NewGuid();

        var result = (await Offload($$"""{ "files": [ { "file": "{{staged.File}}", "documentType": "invoice" } ] }""", id)).Value!;

        var node = result.Payload!.Value.GetProperty("files")[0];
        node.GetProperty("component").GetString().ShouldBe("store-a");
        node.GetProperty("size").GetInt64().ShouldBe(42);
        node.GetProperty("eTag").GetString().ShouldBe("e42");
        node.GetProperty("mimeType").GetString().ShouldBe("image/tiff");
        node.GetProperty("owner").GetProperty("instance").GetString().ShouldBe(id.ToString());
        node.GetProperty("owner").GetProperty("flow").GetString().ShouldBe("kyc");
        node.GetProperty("documentType").GetString().ShouldBe("invoice");
        _staging.Entries.ShouldNotContainKey(staged.File);

        (await Offload($$"""{ "files": [ { "file": "{{staged.File}}" } ] }""")).Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task TicketTakenByAnotherRequestAfterTheLookup_IsRefused_AndTheOthersAreRestored()
    {
        // Two staged uploads; the second is claimed by a concurrent request between this one's lookup and its take.
        var first = Stage("files[]");
        var second = Stage("files[]");
        var staging = Substitute.For<IFileStagingStore>();
        staging.GetAsync("core", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => _staging.Entries.GetValueOrDefault(ci.ArgAt<string>(1)));
        staging.TakeAsync("core", first.File, Arg.Any<CancellationToken>()).Returns(first);
        staging.TakeAsync("core", second.File, Arg.Any<CancellationToken>()).Returns((StagedFile?)null);
        var (sut, workflow) = FileOffloadTestFactory.Create(Master, _store, domain: "core", flow: "kyc", staging: staging);

        var r = await sut.OffloadAsync(new(workflow, Guid.NewGuid(),
            Json($$"""{ "files": [ { "file": "{{first.File}}" }, { "file": "{{second.File}}" } ] }"""), null, FileOffloadMode.External), default);

        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
        await staging.Received(1).RestoreAsync(first, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoreFailureAfterTheTake_PutsTheTicketBack()
    {
        var staged = Stage("files[]");
        _store.PutAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Result.Fail(WorkflowErrors.FileStoreUnavailable("store-a")));

        var r = await Offload($$"""{ "files": [ { "file": "{{staged.File}}" }, { "content": "AQID" } ] }""");

        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
        _staging.Entries.ShouldContainKey(staged.File);
    }

    [Fact]
    public async Task SameUploadTwiceInOneBody_IsRefused_AndStaysStaged()
    {
        var staged = Stage("files[]");
        var r = await Offload($$"""{ "files": [ { "file": "{{staged.File}}" }, { "file": "{{staged.File}}" } ] }""");
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
        _staging.Entries.ShouldContainKey(staged.File);
    }

    [Theory]
    [InlineData("files[]", "other-flow", "store-a")]   // staged for another flow
    [InlineData("streamOnly", "kyc", "store-a")]       // staged for another path
    [InlineData("files[]", "kyc", "store-b")]          // staged for another component
    public async Task StagedUploadOfAnotherFlowPathOrComponent_IsRefused(string path, string flow, string component)
    {
        var staged = Stage(path, flow, component);
        (await Offload($$"""{ "files": [ { "file": "{{staged.File}}" } ] }""")).Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
        _staging.Entries.ShouldContainKey(staged.File);
    }

    [Fact]
    public async Task InlineOnlyField_DoesNotLookAtStagedUploads()
    {
        var staged = Stage("inlineOnly");
        (await Offload($$"""{ "inlineOnly": { "file": "{{staged.File}}" } }""")).Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
    }

    [Fact]
    public async Task StreamOnlyField_RefusesInlineContentFromAClient()
    {
        var r = await Offload("""{ "streamOnly": { "name": "a.pdf", "content": "AQID" } }""");
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
        r.Error.Message!.ShouldContain("streamed uploads only");
        await _store.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task StreamOnlyField_AcceptsRuntimeProducedContent()
        => (await Offload("""{ "streamOnly": { "content": "AQID" } }""", mode: FileOffloadMode.Trusted)).IsSuccess.ShouldBeTrue();

    [Fact]
    public async Task StreamOnlyField_ClaimsAStagedUpload()
    {
        var staged = Stage("streamOnly");
        var r = (await Offload($$"""{ "streamOnly": { "file": "{{staged.File}}" } }""")).Value!;
        r.Payload!.Value.GetProperty("streamOnly").GetProperty("file").GetString().ShouldBe(staged.File);
    }

    [Fact]
    public async Task AnyField_StillAcceptsInlineContent()
        => (await Offload("""{ "files": [ { "content": "AQID" } ] }""")).IsSuccess.ShouldBeTrue();
}

public sealed class FileUploadAppServiceTests
{
    private const string Master = """
        { "type": "object", "properties": {
            "inlineOnly": { "type": "object", "x-storage": { "binding": "store-a" } },
            "files": { "type": "array", "items": { "type": "object", "x-storage": { "binding": "store-a", "transport": "stream" } } } } }
        """;

    private readonly IFileBlobStore _store = Substitute.For<IFileBlobStore>();
    private readonly IFileStagingStore _staging = Substitute.For<IFileStagingStore>();
    private readonly FileUploadAppService _sut;
    private readonly FileStorageOptions _options = new() { Upload = { MaxBytes = 1000, TicketTtlMinutes = 30 } };

    public FileUploadAppServiceTests()
    {
        var (offload, workflow) = FileOffloadTestFactory.Create(Master, _store, "core", "kyc");
        var cache = Substitute.For<IComponentCacheStore>();
        cache.GetFlowAsync("core", "kyc", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Result<Definitions.Workflow>.Ok(workflow));
        var schema = JsonSerializer.Deserialize<SchemaDefinition>($$"""{ "type": "JSON", "schema": {{Master}} }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference("master", "core", "sys-schemas", "1.0.0"));
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("core");
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc));
        _sut = new FileUploadAppService(cache, offload, _store, _staging, runtime, clock, Options.Create(_options),
            NullLogger<FileUploadAppService>.Instance);
    }

    private static FileUploadRequest Request(string path, long? length = 3, string? mime = "image/tiff")
        => new("core", "kyc", null, path, "scan.tiff", mime, new MemoryStream(new byte[] { 1, 2, 3 }), length);

    [Fact]
    public async Task Upload_StreamsIntoTheFieldsStore_AndStagesATicket()
    {
        _store.PutStreamAsync("store-a", Arg.Any<string>(), Arg.Any<Stream>(), 3, "image/tiff", Arg.Any<CancellationToken>())
            .Returns(Result<StoredBlob>.Ok(new StoredBlob(3, "sha")));

        var ticket = (await _sut.UploadAsync(Request("files[]"), default)).Value!;

        ticket.Component.ShouldBe("store-a");
        ticket.Path.ShouldBe("files[]");
        ticket.Size.ShouldBe(3);
        ticket.ETag.ShouldBe("sha");
        ticket.Name.ShouldBe("scan.tiff");
        ticket.ExpiresAt.ShouldBe(new DateTimeOffset(2026, 10, 31, 12, 30, 0, TimeSpan.Zero));
        FileHandle.IsFileId(ticket.File).ShouldBeTrue();
        await _staging.Received(1).SaveAsync(
            Arg.Is<StagedFile>(s => s.File == ticket.File && s.Flow == "kyc" && s.Path == "files[]" && s.Domain == "core" && s.ETag == "sha"),
            TimeSpan.FromMinutes(30), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("inlineOnly", 3L, "inline content only")]
    [InlineData("nope", 3L, "not an x-storage path")]
    [InlineData("files[]", null, "Content-Length")]
    [InlineData("files[]", 1001L, "limit")]
    public async Task Upload_Refused_BeforeAnythingIsStored(string path, long? length, string reason)
    {
        var r = await _sut.UploadAsync(Request(path, length), default);
        r.Error.Code.ShouldBe(WorkflowErrorCodes.FileReferenceInvalid);
        r.Error.Message!.ShouldContain(reason);
        await _store.DidNotReceiveWithAnyArgs().PutStreamAsync(default!, default!, default!, default, default, default);
        await _staging.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default);
    }

    [Fact]
    public async Task StoreFailure_StagesNothing()
    {
        _store.PutStreamAsync(default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Result<StoredBlob>.Fail(WorkflowErrors.FileStoreUnavailable("store-a")));
        (await _sut.UploadAsync(Request("files[]"), default)).Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
        await _staging.DidNotReceiveWithAnyArgs().SaveAsync(default!, default, default);
    }
}
