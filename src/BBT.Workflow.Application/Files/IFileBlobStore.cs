using BBT.Aether.Results;

namespace BBT.Workflow.Files;

/// <summary>
/// Object store behind <c>x-storage</c>. <c>file</c> is the handle's GUID; the key prefix is applied inside. Which
/// provider serves a component (Dapr output binding or direct S3) is configuration (<c>FileStorage:Stores</c>), so
/// callers never branch on it.
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
    /// the caller declared. A provider that cannot stream buffers (bounded by <paramref name="length"/>).
    /// </summary>
    Task<Result<StoredBlob>> PutStreamAsync(
        string component, string file, Stream content, long length, string? mimeType, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the object for reading. A streaming provider answers a seekable stream that fetches ranges lazily; a
    /// buffering provider answers the whole object (<see cref="BlobReadStream.IsBuffered"/>). The caller disposes it.
    /// </summary>
    Task<Result<BlobReadStream>> OpenReadAsync(string component, string file, CancellationToken cancellationToken);
}

/// <summary>What a streamed write stored: the byte count and the lower-case hex sha256 of those bytes.</summary>
public sealed record StoredBlob(long Size, string Sha256);

/// <summary>An opened object: <see cref="Content"/> is <see cref="Length"/> bytes long and seekable.</summary>
/// <param name="IsBuffered">True when <see cref="Content"/> already holds the whole object in memory.</param>
public sealed record BlobReadStream(Stream Content, long Length, bool IsBuffered);
