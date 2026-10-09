using BBT.Aether.Results;

namespace BBT.Workflow.Files;

/// <summary>Object store behind <c>x-storage</c>. <paramref name="file"/> is the handle's GUID; the key prefix is applied inside.</summary>
public interface IFileBlobStore
{
    Task<Result> PutAsync(string component, string file, ReadOnlyMemory<byte> bytes, string? mimeType, CancellationToken cancellationToken);
    Task<Result<byte[]>> GetAsync(string component, string file, CancellationToken cancellationToken);
}
