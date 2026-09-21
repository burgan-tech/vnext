namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Response cache for the <c>human-task</c> domain function.
/// </summary>
public interface IHumanTaskFunctionCache
{
    /// <summary>Whether the cache is enabled.</summary>
    bool Enabled { get; }

    /// <summary>Whether a client-supplied override header is honoured.</summary>
    bool AllowClientOverride { get; }

    /// <summary>
    /// Builds the cache key for this caller in this domain.
    /// </summary>
    /// <remarks>
    /// The key must cover every input the per-instance authorization decision reads, or the cache
    /// can serve one caller scope's answer to another. Role and identity come from the shared
    /// caller scope; the request headers are folded in because dynamic role grants resolve
    /// <c>$.context.Headers.*</c> against header names the WORKFLOW AUTHOR chooses, so they cannot
    /// be enumerated at write time.
    /// </remarks>
    string BuildKey(string domain, IReadOnlyList<string>? roles, IReadOnlyDictionary<string, string?>? headers);

    /// <summary>Reads an entry, or null on a miss or any cache failure.</summary>
    Task<HumanTaskFunctionCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores an entry. Failures are swallowed — a broken cache must not fail the request.</summary>
    Task SetAsync(string key, HumanTaskFunctionCacheEntry entry, CancellationToken cancellationToken = default);
}
