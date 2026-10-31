using System.Text.Json.Serialization;
using BBT.Aether.DistributedCache;

namespace BBT.Workflow.Files;

/// <summary>
/// A streamed upload waiting for a start or transition to reference it. Everything the handle needs except its owner,
/// which is stamped when an instance claims the file. Never carries bytes.
/// </summary>
public sealed class StagedFile
{
    [JsonPropertyName("domain")] public string Domain { get; init; } = string.Empty;
    [JsonPropertyName("flow")] public string Flow { get; init; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("component")] public string Component { get; init; } = string.Empty;
    [JsonPropertyName("file")] public string File { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("eTag")] public string ETag { get; init; } = string.Empty;
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The handle this upload becomes once <paramref name="owner"/> claims it.</summary>
    public FileHandle ToHandle(FileOwner owner) => new(Component, File, Name, MimeType, Size, ETag, owner);
}

/// <summary>Where streamed uploads wait to be claimed (shared by every replica).</summary>
public interface IFileStagingStore
{
    /// <summary>Stages <paramref name="staged"/>; it expires at <see cref="StagedFile.ExpiresAt"/> (<paramref name="ttl"/> from now).</summary>
    Task SaveAsync(StagedFile staged, TimeSpan ttl, CancellationToken cancellationToken);
    Task<StagedFile?> GetAsync(string domain, string file, CancellationToken cancellationToken);
    Task RemoveAsync(string domain, string file, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IFileStagingStore"/> over the distributed cache (Redis): one entry per uploaded file, expiring after
/// <c>FileStorage:Upload:TicketTtlMinutes</c>; removed when an instance claims it. The file id is a random GUID the
/// uploader alone received, so knowing it is the claim.
/// </summary>
public sealed class DistributedFileStagingStore(IDistributedCacheService cache) : IFileStagingStore
{
    internal static string KeyOf(string domain, string file) => $"vnext:{domain}:file-upload:{file}";

    /// <summary>Stores <paramref name="staged"/> until its <see cref="StagedFile.ExpiresAt"/>.</summary>
    public Task SaveAsync(StagedFile staged, TimeSpan ttl, CancellationToken cancellationToken)
        => cache.SetAsync(KeyOf(staged.Domain, staged.File), staged,
            DistributedCacheEntryOptions.WithAbsoluteExpiration(staged.ExpiresAt), cancellationToken);

    public Task<StagedFile?> GetAsync(string domain, string file, CancellationToken cancellationToken)
        => cache.GetAsync<StagedFile>(KeyOf(domain, file), cancellationToken);

    public Task RemoveAsync(string domain, string file, CancellationToken cancellationToken)
        => cache.RemoveAsync(KeyOf(domain, file), cancellationToken);
}
