using System.Text.Json.Serialization;
using BBT.Aether.Clock;
using BBT.Aether.DistributedCache;
using BBT.Aether.DistributedLock;

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

    /// <summary>
    /// Reads and removes the staged upload in one step: of two requests claiming the same file, only one gets it. Null
    /// when it is gone, expired, or being taken by another request at this moment.
    /// </summary>
    Task<StagedFile?> TakeAsync(string domain, string file, CancellationToken cancellationToken);

    /// <summary>Puts back a ticket taken by a request that then failed; nothing when it has expired meanwhile.</summary>
    Task RestoreAsync(StagedFile staged, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IFileStagingStore"/> over the distributed cache (Redis): one entry per uploaded file, expiring after
/// <c>FileStorage:Upload:TicketTtlMinutes</c>; removed when an instance claims it. The file id is a random GUID the
/// uploader alone received, so knowing it is the claim.
/// </summary>
public sealed class DistributedFileStagingStore(
    IDistributedCacheService cache,
    IDistributedLockService lockService,
    IClock clock) : IFileStagingStore
{
    /// <summary>A take is two cache calls; the lock only has to outlive them.</summary>
    private const int TakeLockSeconds = 10;

    internal static string KeyOf(string domain, string file) => $"vnext:{domain}:file-upload:{file}";

    /// <summary>Stores <paramref name="staged"/> until its <see cref="StagedFile.ExpiresAt"/>.</summary>
    public Task SaveAsync(StagedFile staged, TimeSpan ttl, CancellationToken cancellationToken)
        => cache.SetAsync(KeyOf(staged.Domain, staged.File), staged,
            DistributedCacheEntryOptions.WithAbsoluteExpiration(staged.ExpiresAt), cancellationToken);

    public Task<StagedFile?> GetAsync(string domain, string file, CancellationToken cancellationToken)
        => cache.GetAsync<StagedFile>(KeyOf(domain, file), cancellationToken);

    /// <summary>
    /// Get and remove under a lock keyed by the file: the cache has no atomic get-and-delete. A request that cannot take
    /// the lock does not wait: another request is claiming the same file, and only one of them may.
    /// </summary>
    public async Task<StagedFile?> TakeAsync(string domain, string file, CancellationToken cancellationToken)
    {
        var key = KeyOf(domain, file);
        var (acquired, staged) = await lockService.ExecuteWithLockAsync(key + ":claim", async () =>
        {
            var found = await cache.GetAsync<StagedFile>(key, cancellationToken);
            if (found is not null)
                await cache.RemoveAsync(key, cancellationToken);
            return found;
        }, TakeLockSeconds, cancellationToken);
        return acquired ? staged : null;
    }

    public Task RestoreAsync(StagedFile staged, CancellationToken cancellationToken)
    {
        var left = staged.ExpiresAt - new DateTimeOffset(clock.UtcNow, TimeSpan.Zero);
        return left > TimeSpan.Zero ? SaveAsync(staged, left, cancellationToken) : Task.CompletedTask;
    }
}
