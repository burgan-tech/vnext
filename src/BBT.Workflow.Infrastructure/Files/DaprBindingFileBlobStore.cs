using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>
/// <see cref="IFileBlobStore"/> over a Dapr output binding. Sends raw bytes over gRPC (no base64 metadata), and both
/// <c>key</c> (bindings.aws.s3) and <c>fileName</c> (bindings.localstorage) so one call shape fits either component.
/// Deliberately not <c>DaprBindingTaskInvoker</c>: that one JSON-deserializes the body and drops the response.
/// </summary>
public sealed class DaprBindingFileBlobStore(
    DaprClient daprClient,
    IOptions<FileStorageOptions> options,
    ILogger<DaprBindingFileBlobStore> logger) : IFileBlobStore
{
    private readonly string _prefix = options.Value.KeyPrefix ?? string.Empty;

    public async Task<Result> PutAsync(string component, string file, ReadOnlyMemory<byte> bytes, string? mimeType, CancellationToken cancellationToken)
    {
        var request = new BindingRequest(component, "create") { Data = bytes };
        AddKey(request, file);
        // bindings.aws.s3 reads exactly "Content-Type" (components-contrib metatadataContentType); bindings.localstorage ignores it.
        if (!string.IsNullOrWhiteSpace(mimeType))
            request.Metadata["Content-Type"] = mimeType;
        try
        {
            await daprClient.InvokeBindingAsync(request, cancellationToken);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.FileStoreFailed(ex, component, "create", Guid.Empty);
            return Result.Fail(WorkflowErrors.FileStoreUnavailable(component));
        }
    }

    public async Task<Result<byte[]>> GetAsync(string component, string file, CancellationToken cancellationToken)
    {
        var request = new BindingRequest(component, "get");
        AddKey(request, file);
        try
        {
            var response = await daprClient.InvokeBindingAsync(request, cancellationToken);
            return Result<byte[]>.Ok(response.Data.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.FileStoreFailed(ex, component, "get", Guid.Empty);
            return Result<byte[]>.Fail(WorkflowErrors.FileStoreUnavailable(component));
        }
    }

    /// <summary>
    /// A Dapr output binding takes one gRPC message, so the body is buffered (bounded by <paramref name="length"/>,
    /// itself bounded by <c>FileStorage:Upload:MaxBytes</c> and the sidecar's <c>--max-body-size</c>) and hashed, then
    /// stored with <see cref="PutAsync"/>.
    /// </summary>
    public async Task<Result<StoredBlob>> PutStreamAsync(
        string component, string file, Stream content, long length, string? mimeType, CancellationToken cancellationToken)
    {
        if (length is < 0 or > int.MaxValue)
            return Result<StoredBlob>.Fail(WorkflowErrors.FileStoreUnavailable(component));

        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await content.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0)
                break;
            read += n;
        }
        // A body shorter or longer than its declared length is not stored.
        if (read != buffer.Length || await content.ReadAsync(new byte[1], cancellationToken) != 0)
            return Result<StoredBlob>.Fail(WorkflowErrors.FileReferenceInvalid("body", "the body length does not match Content-Length"));

        var put = await PutAsync(component, file, buffer, mimeType, cancellationToken);
        return put.IsSuccess
            ? Result<StoredBlob>.Ok(new StoredBlob(length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer))))
            : Result<StoredBlob>.Fail(put.Error);
    }

    private void AddKey(BindingRequest request, string file)
    {
        var key = _prefix + file;
        request.Metadata["key"] = key;
        request.Metadata["fileName"] = key;
    }
}
