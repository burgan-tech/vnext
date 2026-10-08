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
        if (!string.IsNullOrWhiteSpace(mimeType))
            request.Metadata["contentType"] = mimeType;
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

    private void AddKey(BindingRequest request, string file)
    {
        var key = _prefix + file;
        request.Metadata["key"] = key;
        request.Metadata["fileName"] = key;
    }
}
