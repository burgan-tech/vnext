using System.Net;
using System.Runtime.InteropServices;
using Amazon.S3;
using Amazon.S3.Model;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>
/// <see cref="IFileBlobStore"/> over the S3 API, for components configured with <c>Provider: s3</c>.
/// <list type="bullet">
/// <item>Writes stream the body with a known <c>Content-Length</c>. Over HTTPS the payload is sent as is
/// (<c>UNSIGNED-PAYLOAD</c>, TLS is the integrity layer) with no checksum trailer, so the stored object is byte for
/// byte the file even on a server that does not decode aws-chunked (vnext-client-sdk-core#145). Over plain HTTP the
/// SDK must sign the payload, which for a stream means signed aws-chunked chunks; use HTTP only for a local store
/// that decodes them (MinIO does).</item>
/// <item>Every write is checked with a HEAD: the stored size must equal the bytes sent, otherwise the object is
/// deleted and the write fails (503).</item>
/// <item>Reads open a lazily ranged stream; a missing object is <c>FileNotFound</c> (404), not an outage.</item>
/// </list>
/// </summary>
public sealed class S3FileBlobStore(
    IS3ClientFactory clients,
    IOptions<FileStorageOptions> options,
    ILogger<S3FileBlobStore> logger) : IFileBlobStore
{
    private readonly FileStorageOptions _options = options.Value;

    public async Task<Result> PutAsync(string component, string file, ReadOnlyMemory<byte> bytes, string? mimeType, CancellationToken cancellationToken)
    {
        var stream = MemoryMarshal.TryGetArray(bytes, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);
        await using (stream)
        {
            var put = await PutObjectAsync(component, file, stream, bytes.Length, mimeType, cancellationToken);
            return put.IsSuccess ? Result.Ok() : Result.Fail(put.Error);
        }
    }

    public async Task<Result<StoredBlob>> PutStreamAsync(
        string component, string file, Stream content, long length, string? mimeType, CancellationToken cancellationToken)
    {
        await using var hashing = new HashingReadStream(content, length);
        var put = await PutObjectAsync(component, file, hashing, length, mimeType, cancellationToken);
        if (!put.IsSuccess)
            return Result<StoredBlob>.Fail(put.Error);
        if (hashing.BytesRead != length)
        {
            logger.FileStoredSizeMismatch(component, file, hashing.BytesRead, length);
            await TryDeleteAsync(component, file, cancellationToken);
            return Result<StoredBlob>.Fail(WorkflowErrors.FileStoreUnavailable(component));
        }
        return Result<StoredBlob>.Ok(new StoredBlob(length, hashing.Sha256));
    }

    public async Task<Result<byte[]>> GetAsync(string component, string file, CancellationToken cancellationToken)
    {
        var (client, store) = ClientOf(component);
        try
        {
            using var response = await client.GetObjectAsync(store.Bucket, Key(file), cancellationToken);
            var buffer = new MemoryStream(response.ContentLength is > 0 and <= int.MaxValue ? (int)response.ContentLength : 0);
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return Result<byte[]>.Ok(buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray());
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return Result<byte[]>.Fail(WorkflowErrors.FileNotFound(file));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.FileStoreFailed(ex, component, "get", Guid.Empty);
            return Result<byte[]>.Fail(WorkflowErrors.FileStoreUnavailable(component));
        }
    }

    public async Task<Result<BlobReadStream>> OpenReadAsync(string component, string file, CancellationToken cancellationToken)
    {
        var (client, store) = ClientOf(component);
        var key = Key(file);
        try
        {
            var head = await client.GetObjectMetadataAsync(store.Bucket, key, cancellationToken);
            var length = head.ContentLength;
            return Result<BlobReadStream>.Ok(
                new BlobReadStream(new S3RangedReadStream(client, store.Bucket!, key, length), length, IsBuffered: false));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return Result<BlobReadStream>.Fail(WorkflowErrors.FileNotFound(file));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.FileStoreFailed(ex, component, "head", Guid.Empty);
            return Result<BlobReadStream>.Fail(WorkflowErrors.FileStoreUnavailable(component));
        }
    }

    private async Task<Result> PutObjectAsync(
        string component, string file, Stream body, long length, string? mimeType, CancellationToken cancellationToken)
    {
        var (client, store) = ClientOf(component);
        var key = Key(file);
        var https = IsHttps(store);
        var request = new PutObjectRequest
        {
            BucketName = store.Bucket,
            Key = key,
            InputStream = body,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            ContentType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType,
            // HTTPS: raw body, no chunk framing, no trailer. HTTP: the SDK has to sign the payload of a stream it cannot
            // rewind, which it does with signed aws-chunked chunks.
            DisablePayloadSigning = https ? true : null,
            UseChunkEncoding = !https,
        };
        request.Headers.ContentLength = length;

        try
        {
            await client.PutObjectAsync(request, cancellationToken);
            var head = await client.GetObjectMetadataAsync(store.Bucket, key, cancellationToken);
            if (head.ContentLength != length)
            {
                logger.FileStoredSizeMismatch(component, file, head.ContentLength, length);
                await TryDeleteAsync(component, file, cancellationToken);
                return Result.Fail(WorkflowErrors.FileStoreUnavailable(component));
            }
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.FileStoreFailed(ex, component, "create", Guid.Empty);
            return Result.Fail(WorkflowErrors.FileStoreUnavailable(component));
        }
    }

    private async Task TryDeleteAsync(string component, string file, CancellationToken cancellationToken)
    {
        var (client, store) = ClientOf(component);
        try
        {
            await client.DeleteObjectAsync(store.Bucket, Key(file), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.FileStoreFailed(ex, component, "delete", Guid.Empty);
        }
    }

    private (IAmazonS3 Client, FileStoreOptions Store) ClientOf(string component)
    {
        var store = _options.StoreOf(component);
        return (clients.Get(component, store), store);
    }

    private string Key(string file) => _options.KeyPrefix + file;

    private static bool IsHttps(FileStoreOptions store)
        => string.IsNullOrWhiteSpace(store.ServiceUrl)
           || store.ServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
