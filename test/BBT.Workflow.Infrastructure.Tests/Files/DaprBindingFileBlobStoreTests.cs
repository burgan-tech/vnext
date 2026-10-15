using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Files;
using Dapr;
using Dapr.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Files;

public sealed class DaprBindingFileBlobStoreTests
{
    private readonly DaprClient _dapr = Substitute.For<DaprClient>();

    private DaprBindingFileBlobStore Store(string prefix = "vnext-runtime/") =>
        new(_dapr, Options.Create(new FileStorageOptions { KeyPrefix = prefix }), NullLogger<DaprBindingFileBlobStore>.Instance);

    [Fact]
    public async Task Put_SendsRawBytesWithBothKeyMetadata()
    {
        BindingRequest? sent = null;
        _dapr.InvokeBindingAsync(Arg.Do<BindingRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new BindingResponse(new BindingRequest("x", "create"), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>()));

        (await Store().PutAsync("vnext-blob-s3", "f1", new byte[] { 1, 2 }, "application/pdf", default)).IsSuccess.ShouldBeTrue();

        sent!.BindingName.ShouldBe("vnext-blob-s3");
        sent.Operation.ShouldBe("create");
        sent.Data.ToArray().ShouldBe(new byte[] { 1, 2 });
        sent.Metadata["key"].ShouldBe("vnext-runtime/f1");
        sent.Metadata["fileName"].ShouldBe("vnext-runtime/f1");
        sent.Metadata["contentType"].ShouldBe("application/pdf");
    }

    [Fact]
    public async Task Put_BindingThrows_ReturnsFileStoreUnavailable()
    {
        _dapr.InvokeBindingAsync(Arg.Any<BindingRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new DaprException("down"));
        var result = await Store().PutAsync("c", "f", new byte[] { 1 }, null, default);
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
    }

    [Fact]
    public async Task Get_ReturnsData()
    {
        _dapr.InvokeBindingAsync(Arg.Is<BindingRequest>(r => r.Operation == "get" && r.Metadata["key"] == "f"), Arg.Any<CancellationToken>())
            .Returns(new BindingResponse(new BindingRequest("c", "get"), new byte[] { 9 }, new Dictionary<string, string>()));
        (await Store(prefix: "").GetAsync("c", "f", default)).Value.ShouldBe(new byte[] { 9 });
    }
}
