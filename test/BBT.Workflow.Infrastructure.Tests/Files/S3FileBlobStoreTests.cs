using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BBT.Workflow.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Files;

/// <summary>
/// The S3 provider (vnext-client-sdk-core#145 + streaming): over HTTPS the body goes out raw (UNSIGNED-PAYLOAD, no
/// chunk framing, no checksum trailer); every write is checked with a HEAD; size/eTag come from the bytes read;
/// reads are lazily ranged; a missing object is 404.
/// </summary>
public sealed class S3FileBlobStoreTests
{
    private const string Component = "vnext-blob-s3";
    private readonly IAmazonS3 _client = Substitute.For<IAmazonS3>();
    private readonly List<PutObjectRequest> _puts = [];
    private readonly Dictionary<string, byte[]> _objects = new();

    private S3FileBlobStore Store(string serviceUrl = "https://s3.example.com", string prefix = "rt/")
    {
        var options = new FileStorageOptions { KeyPrefix = prefix };
        options.Stores[Component] = new FileStoreOptions { Provider = "s3", ServiceUrl = serviceUrl, Bucket = "docs" };
        var factory = Substitute.For<IS3ClientFactory>();
        factory.Get(Component, Arg.Any<FileStoreOptions>()).Returns(_client);
        return new S3FileBlobStore(factory, Options.Create(options), NullLogger<S3FileBlobStore>.Instance);
    }

    public S3FileBlobStoreTests()
    {
        _client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            var request = ci.Arg<PutObjectRequest>();
            _puts.Add(request);
            var copy = new MemoryStream();
            await request.InputStream.CopyToAsync(copy);
            _objects[request.Key] = copy.ToArray();
            return new PutObjectResponse();
        });
        _client.GetObjectMetadataAsync("docs", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var key = ci.ArgAt<string>(1);
            if (!_objects.TryGetValue(key, out var bytes))
                throw new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound };
            return new GetObjectMetadataResponse { ContentLength = bytes.LongLength };
        });
        _client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<GetObjectRequest>();
            var bytes = _objects[request.Key];
            var start = request.ByteRange?.Start ?? 0;
            var end = request.ByteRange?.End ?? bytes.Length - 1;
            var slice = bytes[(int)start..((int)end + 1)];
            return new GetObjectResponse { ResponseStream = new MemoryStream(slice), ContentLength = slice.Length };
        });
        _client.GetObjectAsync("docs", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var key = ci.ArgAt<string>(1);
            if (!_objects.TryGetValue(key, out var bytes))
                throw new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound };
            return new GetObjectResponse { ResponseStream = new MemoryStream(bytes), ContentLength = bytes.Length };
        });
    }

    private static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public async Task Https_SendsTheRawBody_WithoutChunkEncodingOrPayloadSignature()
    {
        var file = Guid.NewGuid().ToString();
        (await Store().PutAsync(Component, file, Bytes(6265), "application/pdf", default)).IsSuccess.ShouldBeTrue();

        var put = _puts.ShouldHaveSingleItem();
        put.BucketName.ShouldBe("docs");
        put.Key.ShouldBe("rt/" + file);
        put.DisablePayloadSigning.ShouldBe(true);
        put.UseChunkEncoding.ShouldBeFalse();
        put.Headers.ContentLength.ShouldBe(6265);
        put.ContentType.ShouldBe("application/pdf");
        _objects["rt/" + file].ShouldBe(Bytes(6265));
    }

    [Fact]
    public async Task Http_SignsThePayloadWithChunkEncoding()
    {
        (await Store("http://minio:9000").PutAsync(Component, Guid.NewGuid().ToString(), Bytes(10), null, default)).IsSuccess.ShouldBeTrue();
        var put = _puts.ShouldHaveSingleItem();
        put.DisablePayloadSigning.ShouldBeNull();
        put.UseChunkEncoding.ShouldBeTrue();
        put.ContentType.ShouldBe("application/octet-stream");
    }

    [Fact]
    public async Task StoredSizeDifferentFromSent_DeletesTheObject_And503()
    {
        _client.GetObjectMetadataAsync("docs", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectMetadataResponse { ContentLength = 6309 });
        var file = Guid.NewGuid().ToString();

        var result = await Store().PutAsync(Component, file, Bytes(6265), null, default);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
        await _client.Received(1).DeleteObjectAsync("docs", "rt/" + file, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutStream_ReturnsSizeAndSha256OfTheBytesRead()
    {
        var bytes = Bytes(100_000);
        var stored = await Store().PutStreamAsync(Component, Guid.NewGuid().ToString(), new MemoryStream(bytes), bytes.Length, "image/tiff", default);

        stored.IsSuccess.ShouldBeTrue();
        stored.Value!.Size.ShouldBe(100_000);
        stored.Value.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public async Task PutStream_ShorterThanDeclared_IsRefusedAndDeleted()
    {
        var file = Guid.NewGuid().ToString();
        _client.GetObjectMetadataAsync("docs", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectMetadataResponse { ContentLength = 10 });

        var stored = await Store().PutStreamAsync(Component, file, new MemoryStream(Bytes(5)), 10, null, default);

        stored.IsSuccess.ShouldBeFalse();
        await _client.Received(1).DeleteObjectAsync("docs", "rt/" + file, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutStream_LongerThanDeclared_Fails()
    {
        var stored = await Store().PutStreamAsync(Component, Guid.NewGuid().ToString(), new MemoryStream(Bytes(20)), 10, null, default);
        stored.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
    }

    [Fact]
    public async Task OpenRead_MissingObject_IsNotFound()
        => (await Store().OpenReadAsync(Component, Guid.NewGuid().ToString(), default)).Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);

    [Fact]
    public async Task Get_MissingObject_IsNotFound()
        => (await Store().GetAsync(Component, Guid.NewGuid().ToString(), default)).Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);

    [Fact]
    public async Task OpenRead_IsSeekable_AndFetchesRangesLazily()
    {
        var file = Guid.NewGuid().ToString();
        var bytes = Bytes(1000);
        _objects["rt/" + file] = bytes;

        var opened = (await Store().OpenReadAsync(Component, file, default)).Value!;
        opened.IsBuffered.ShouldBeFalse();
        opened.Length.ShouldBe(1000);
        await _client.DidNotReceiveWithAnyArgs().GetObjectAsync(Arg.Any<GetObjectRequest>(), default);

        await using var stream = opened.Content;
        stream.CanSeek.ShouldBeTrue();
        stream.Seek(900, SeekOrigin.Begin);
        var tail = new byte[100];
        (await stream.ReadAsync(tail)).ShouldBe(100);
        tail.ShouldBe(bytes[900..]);
        (await stream.ReadAsync(new byte[10])).ShouldBe(0);

        await _client.Received(1).GetObjectAsync(
            Arg.Is<GetObjectRequest>(r => r.ByteRange.Start == 900 && r.ByteRange.End == 999), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_ReturnsTheWholeObject()
    {
        var file = Guid.NewGuid().ToString();
        _objects["rt/" + file] = Bytes(4096);
        (await Store().GetAsync(Component, file, default)).Value.ShouldBe(Bytes(4096));
    }

    [Fact]
    public void ClientConfig_SendsAndValidatesChecksumsOnlyWhenRequired()
    {
        var config = S3ClientFactory.ConfigOf(new FileStoreOptions { Provider = "s3", ServiceUrl = "https://s3.example.com", Bucket = "b" });
        config.RequestChecksumCalculation.ShouldBe(RequestChecksumCalculation.WHEN_REQUIRED);
        config.ResponseChecksumValidation.ShouldBe(ResponseChecksumValidation.WHEN_REQUIRED);
        config.ForcePathStyle.ShouldBeTrue();
    }
}

public sealed class RoutingFileBlobStoreTests
{
    [Fact]
    public async Task ComponentWithoutAnEntry_GoesToTheDaprBinding_AnS3Entry_ToS3()
    {
        var options = new FileStorageOptions();
        options.Stores["s3-store"] = new FileStoreOptions { Provider = "S3", Bucket = "b" };
        var dapr = Substitute.For<global::Dapr.Client.DaprClient>();
        dapr.InvokeBindingAsync(Arg.Any<global::Dapr.Client.BindingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new global::Dapr.Client.BindingResponse(new global::Dapr.Client.BindingRequest("x", "get"), new byte[] { 1 }, new Dictionary<string, string>()));
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetObjectAsync("b", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new GetObjectResponse { ResponseStream = new MemoryStream(new byte[] { 2 }), ContentLength = 1 });
        var factory = Substitute.For<IS3ClientFactory>();
        factory.Get("s3-store", Arg.Any<FileStoreOptions>()).Returns(s3);
        var o = Options.Create(options);
        var sut = new RoutingFileBlobStore(
            new DaprBindingFileBlobStore(dapr, o, NullLogger<DaprBindingFileBlobStore>.Instance),
            new S3FileBlobStore(factory, o, NullLogger<S3FileBlobStore>.Instance), o);

        (await sut.GetAsync("vnext-blob-local", "f", default)).Value.ShouldBe(new byte[] { 1 });
        (await sut.GetAsync("s3-store", "f", default)).Value.ShouldBe(new byte[] { 2 });
    }
}

public sealed class FileStorageOptionsValidatorTests
{
    [Fact]
    public void NoStores_IsValid() => new FileStorageOptionsValidator().Validate(null, new FileStorageOptions()).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("s3", null, "Bucket is required")]
    [InlineData("gcs", "b", "must be 'dapr' or 's3'")]
    public void BrokenStore_FailsTheStart(string provider, string? bucket, string message)
    {
        var options = new FileStorageOptions();
        options.Stores["x"] = new FileStoreOptions { Provider = provider, Bucket = bucket };
        var result = new FileStorageOptionsValidator().Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(message);
    }
}

public sealed class DaprBindingStreamTests
{
    [Fact]
    public async Task PutStream_BuffersHashesAndStores_OpenRead_IsBuffered()
    {
        var dapr = Substitute.For<global::Dapr.Client.DaprClient>();
        byte[]? sent = null;
        dapr.InvokeBindingAsync(Arg.Do<global::Dapr.Client.BindingRequest>(r => { if (r.Operation == "create") sent = r.Data.ToArray(); }), Arg.Any<CancellationToken>())
            .Returns(ci => new global::Dapr.Client.BindingResponse(ci.Arg<global::Dapr.Client.BindingRequest>(), sent ?? [], new Dictionary<string, string>()));
        var store = new DaprBindingFileBlobStore(dapr, Options.Create(new FileStorageOptions()), NullLogger<DaprBindingFileBlobStore>.Instance);
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();

        var stored = await store.PutStreamAsync("c", "f", new MemoryStream(bytes), bytes.Length, null, default);

        stored.Value!.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        sent.ShouldBe(bytes);
        var opened = (await store.OpenReadAsync("c", "f", default)).Value!;
        opened.IsBuffered.ShouldBeTrue();
        opened.Length.ShouldBe(5000);
    }

    [Fact]
    public async Task PutStream_LengthMismatch_IsRefusedWithoutAStoreCall()
    {
        var dapr = Substitute.For<global::Dapr.Client.DaprClient>();
        var store = new DaprBindingFileBlobStore(dapr, Options.Create(new FileStorageOptions()), NullLogger<DaprBindingFileBlobStore>.Instance);

        (await store.PutStreamAsync("c", "f", new MemoryStream(new byte[5]), 10, null, default)).IsSuccess.ShouldBeFalse();
        (await store.PutStreamAsync("c", "f", new MemoryStream(new byte[15]), 10, null, default)).IsSuccess.ShouldBeFalse();
        await dapr.DidNotReceiveWithAnyArgs().InvokeBindingAsync(Arg.Any<global::Dapr.Client.BindingRequest>(), default);
    }
}
