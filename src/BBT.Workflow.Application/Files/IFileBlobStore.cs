using BBT.Aether.Results;

namespace BBT.Workflow.Files;

/// <summary>
/// Object store behind <c>x-storage</c>: the Dapr output binding named by the component, never a store SDK. <c>file</c>
/// is the handle's GUID; the key prefix is applied inside.
/// </summary>
public interface IFileBlobStore
{
    /// <summary>Stores <paramref name="bytes"/> under <paramref name="file"/>.</summary>
    Task<Result> PutAsync(string component, string file, ReadOnlyMemory<byte> bytes, string? mimeType, CancellationToken cancellationToken);

    /// <summary>Reads the whole object into memory.</summary>
    Task<Result<byte[]>> GetAsync(string component, string file, CancellationToken cancellationToken);

    /// <summary>
    /// Stores exactly <paramref name="length"/> bytes read from <paramref name="content"/>, and returns the count and
    /// the sha256 of the bytes it read — the handle's <c>size</c> and <c>eTag</c> are what was stored, never a value
    /// the caller declared. A Dapr binding takes one message, so the bytes are buffered (bounded by <paramref name="length"/>).
    /// </summary>
    Task<Result<StoredBlob>> PutStreamAsync(
        string component, string file, Stream content, long length, string? mimeType, CancellationToken cancellationToken);
}

/// <summary>What a streamed write stored: the byte count and the lower-case hex sha256 of those bytes.</summary>
public sealed record StoredBlob(long Size, string Sha256);
