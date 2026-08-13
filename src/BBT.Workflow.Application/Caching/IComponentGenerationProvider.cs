namespace BBT.Workflow.Caching;

/// <summary>
/// Supplies and replaces the per-component <b>generation token</b> that scopes cached version
/// resolutions.
/// </summary>
/// <remarks>
/// A resolution entry answers a version <i>request</i> (<c>latest</c>, <c>1</c>, <c>1.2</c>,
/// <c>2.3.5</c>), so its value depends on the whole set of published versions rather than on any single
/// component revision. Publishing or deactivating a version can therefore change it, and a publish is
/// not monotonic — a lower version may be released.
/// <para>
/// Rather than try to work out which cached answers a publish invalidates, resolution entries are keyed
/// under this token and the token is replaced whenever the published set changes. Every prior entry for
/// that component becomes unreachable at once, including request spellings that could never have been
/// enumerated from the published version (for example the leading-zero package-version aliases
/// <see cref="Instances.InstanceDataVersionComparer.FindBestMatch"/> accepts).
/// </para>
/// <para>
/// The token only has to <i>differ</i> from its predecessor; it carries no ordering. That is what makes
/// concurrent publishes safe: whichever write lands last, the resulting token differs from the one the
/// stale entries were written under, so invalidation holds either way. A race costs at most one
/// duplicate backend load — never a stale read.
/// </para>
/// </remarks>
public interface IComponentGenerationProvider
{
    /// <summary>
    /// Gets the component's current generation token, creating one if none exists.
    /// </summary>
    /// <param name="componentTypeKey">The component type key (e.g. <c>sys-views</c>)</param>
    /// <param name="domain">The domain identifier</param>
    /// <param name="key">The component key</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>The current token. Never null or empty.</returns>
    /// <remarks>
    /// Two callers bootstrapping concurrently may produce two different tokens. This is harmless: each
    /// resolves correctly against the backend, and at worst one extra load is performed. If the
    /// distributed cache is unreachable this returns a fresh token per call, which degrades reads to
    /// backend loads rather than serving anything stale.
    /// </remarks>
    Task<string> GetAsync(
        string componentTypeKey,
        string domain,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the component's generation token, invalidating every cached version resolution for it.
    /// </summary>
    /// <param name="componentTypeKey">The component type key (e.g. <c>sys-views</c>)</param>
    /// <param name="domain">The domain identifier</param>
    /// <param name="key">The component key</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>
    /// The new token, so a caller that already holds freshly resolved values can write them under it.
    /// </returns>
    /// <remarks>
    /// If the token cannot be written it is removed instead, since an absent token forces the next
    /// reader to bootstrap and therefore invalidates just as effectively. Only when both the write and
    /// the removal fail can stale resolutions remain reachable; that case is logged at error level and
    /// is bounded by <see cref="ComponentCacheOptions.GenerationTtlSeconds"/>.
    /// </remarks>
    Task<string> BumpAsync(
        string componentTypeKey,
        string domain,
        string key,
        CancellationToken cancellationToken = default);
}
