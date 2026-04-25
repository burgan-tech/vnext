namespace BBT.Workflow.Caching;

/// <summary>
/// Two-tier Redis caching strategy: maintains a lightweight index of known versions
/// per component (vidx:{componentKey}:{domain}:{key}) so that partial or "latest" version
/// lookups can be resolved against Redis instead of falling through to the database
/// to enumerate all versions. The component body itself is stored under a separate
/// fully-versioned key by <see cref="ICacheSet{T}"/>.
/// </summary>
public interface IComponentVersionIndex
{
    /// <summary>
    /// Returns all known versions of a component, ordered by SemVer.
    /// Returns null when the index entry is missing in Redis - the caller is expected
    /// to fall back to the backend (DB) and repopulate the index.
    /// </summary>
    /// <param name="componentKey">Logical component group (e.g. "workflow", "workflowtask").</param>
    /// <param name="domain">Domain identifier.</param>
    /// <param name="key">Component key.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    Task<SortedSet<string>?> GetVersionsAsync(
        string componentKey,
        string domain,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a version to the index when a new component version is published or
    /// observed via a backend load.
    /// </summary>
    Task AddVersionAsync(
        string componentKey,
        string domain,
        string key,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a single version from the index (invalidation).
    /// </summary>
    Task RemoveVersionAsync(
        string componentKey,
        string domain,
        string key,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the entire index entry (used when the component is fully removed).
    /// </summary>
    Task RemoveAllVersionsAsync(
        string componentKey,
        string domain,
        string key,
        CancellationToken cancellationToken = default);
}
