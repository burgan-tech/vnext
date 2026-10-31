using BBT.Aether.Results;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>
/// The registered <see cref="IFileBlobStore"/>: sends each call to the provider configured for its component
/// (<c>FileStorage:Stores:{component}:Provider</c>) — <see cref="S3FileBlobStore"/> for <c>s3</c>,
/// <see cref="DaprBindingFileBlobStore"/> otherwise (including every component with no entry).
/// </summary>
public sealed class RoutingFileBlobStore(
    DaprBindingFileBlobStore dapr,
    S3FileBlobStore s3,
    IOptions<FileStorageOptions> options) : IFileBlobStore
{
    private IFileBlobStore For(string component) => options.Value.StoreOf(component).IsS3 ? s3 : dapr;

    public Task<Result> PutAsync(string component, string file, ReadOnlyMemory<byte> bytes, string? mimeType, CancellationToken cancellationToken)
        => For(component).PutAsync(component, file, bytes, mimeType, cancellationToken);

    public Task<Result<byte[]>> GetAsync(string component, string file, CancellationToken cancellationToken)
        => For(component).GetAsync(component, file, cancellationToken);

    public Task<Result<StoredBlob>> PutStreamAsync(
        string component, string file, Stream content, long length, string? mimeType, CancellationToken cancellationToken)
        => For(component).PutStreamAsync(component, file, content, length, mimeType, cancellationToken);

    public Task<Result<BlobReadStream>> OpenReadAsync(string component, string file, CancellationToken cancellationToken)
        => For(component).OpenReadAsync(component, file, cancellationToken);
}
