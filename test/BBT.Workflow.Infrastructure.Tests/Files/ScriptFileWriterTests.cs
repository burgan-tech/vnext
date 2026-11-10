using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Files;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Functions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Files;

/// <summary>
/// vnext-client-sdk-core#142-A: a script writes a processed file back as an x-storage handle of its instance, into a
/// component its flow may use; FileObjectKey names the object a binding task deletes (vnext-client-sdk-core#142-D).
/// </summary>
public sealed class ScriptFileWriterTests
{
    private readonly IFileBlobStore _store = Substitute.For<IFileBlobStore>();
    private readonly IFileOffloadService _offload = Substitute.For<IFileOffloadService>();
    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly IRuntimeInfoProvider _runtime = Substitute.For<IRuntimeInfoProvider>();
    private readonly FileStorageOptions _options = new() { KeyPrefix = "rt/", AllowedBindings = ["shared-store"] };

    public ScriptFileWriterTests()
    {
        _runtime.IsDomainMatch("core").Returns(true);
        var workflow = global::BBT.Workflow.Definitions.Workflow.Create();
        workflow.SetReference(new Reference("kyc", "core", "sys-flows", "1.0.0"));
        _cache.GetFlowAsync("core", "kyc", null, Arg.Any<CancellationToken>()).Returns(Result<global::BBT.Workflow.Definitions.Workflow>.Ok(workflow));
        _offload.GetFieldsAsync(Arg.Any<global::BBT.Workflow.Definitions.Workflow>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<FileStorageField>>.Ok([new FileStorageField(["converted"], "flow-store")]));
        _store.PutStreamAsync(default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(ci => Result<StoredBlob>.Ok(new StoredBlob(ci.ArgAt<long>(3), "sha-of-bytes")));
    }

    private ScriptFileWriter Writer() => new(_store, _cache, _offload, _runtime, Options.Create(_options), NullLogger<ScriptFileWriter>.Instance);

    [Theory]
    [InlineData("flow-store")]     // declared by the flow's master schema
    [InlineData("shared-store")]   // FileStorage:AllowedBindings
    public async Task Write_StoresIntoAnAllowedComponent_AndReturnsTheHandleOfTheOwner(string component)
    {
        var handle = await Writer().WriteAsync("core", "kyc", "i-1", component, new MemoryStream(new byte[7]), 7, "out.pdf", "application/pdf", default);

        handle.Component.ShouldBe(component);
        FileHandle.IsFileId(handle.File).ShouldBeTrue();
        handle.Size.ShouldBe(7);
        handle.ETag.ShouldBe("sha-of-bytes");
        handle.Name.ShouldBe("out.pdf");
        handle.MimeType.ShouldBe("application/pdf");
        handle.Owner.ShouldBe(new ScriptFileOwner("core", "kyc", "i-1"));
        await _store.Received(1).PutStreamAsync(component, handle.File, Arg.Any<Stream>(), 7, "application/pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Write_HandleSerializesInThePersistedShape_SoTheTrustedSwapAcceptsIt()
    {
        var handle = await Writer().WriteAsync("core", "kyc", Guid.NewGuid().ToString(), "flow-store", new MemoryStream(new byte[3]), 3, null, null, default);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(handle))!.AsObject();
        FileHandle.TryRead(node).ShouldNotBeNull().File.ShouldBe(handle.File);
    }

    [Fact]
    public async Task Write_ForeignComponent_Throws_WithoutStoring()
    {
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Writer().WriteAsync("core", "kyc", "i", "someone-elses-bucket", new MemoryStream(new byte[1]), 1, null, null, default));
        await _store.DidNotReceiveWithAnyArgs().PutStreamAsync(default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task Write_ForADomainNotServedHere_Throws()
        => await Should.ThrowAsync<InvalidOperationException>(() =>
            Writer().WriteAsync("other", "kyc", "i", "flow-store", new MemoryStream(new byte[1]), 1, null, null, default));

    [Fact]
    public async Task Write_AboveTheUploadLimit_Throws()
    {
        _options.Upload.MaxBytes = 5;
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Writer().WriteAsync("core", "kyc", "i", "flow-store", new MemoryStream(new byte[6]), 6, null, null, default));
    }

    [Fact]
    public void ObjectKey_IsThePrefixedFileId_AndOnlyForAFileId()
    {
        var file = Guid.NewGuid().ToString();
        Writer().ObjectKey(file).ShouldBe("rt/" + file);
        Should.Throw<ArgumentException>(() => Writer().ObjectKey("../../etc/passwd"));
    }
}

public sealed class ScriptFileTests
{
    [Fact]
    public void OpenRead_IsAReadOnlySeekableStreamOverTheBytes_NewOnEachCall()
    {
        var file = new ScriptFile(new byte[] { 1, 2, 3 }, "a", "b", 3, "e");
        using var first = file.OpenRead();
        using var second = file.OpenRead();
        first.CanWrite.ShouldBeFalse();
        first.CanSeek.ShouldBeTrue();
        first.ReadByte().ShouldBe(1);
        second.Position.ShouldBe(0);
    }
}
