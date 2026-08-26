using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Caching;

/// <summary>
/// Configuration options for the component definition cache (workflows, tasks, schemas, functions,
/// views, extensions, mappings).
/// </summary>
/// <remarks>
/// The cache holds two categories of entry, and they have different correctness rules:
/// <list type="bullet">
///     <item><description>
///         <b>Immutable bodies</b> — keyed by a canonical full version
///         (<c>MAJOR.MINOR.PATCH-pkg.PKG_VERSION</c>). A full version identifies exactly one component
///         revision forever, so these may be written unconditionally and their TTL is nothing more
///         than a memory bound.
///     </description></item>
///     <item><description>
///         <b>Resolution entries</b> — the answer to a version <i>request</i> such as <c>latest</c>,
///         <c>1</c>, <c>1.2</c> or <c>2.3.5</c>. Their value is a function of the whole set of
///         published versions, so publishing or deactivating any version can change them. They must
///         never be written as though the component being published wins the range: a publish is not
///         monotonic (a lower version may be released, and a version may be deactivated).
///     </description></item>
/// </list>
/// Resolution entries are therefore keyed under a per-component <b>generation token</b>. Publishing or
/// invalidating writes a fresh token, which makes every prior resolution entry for that component
/// unreachable at once — including request spellings no publish could have enumerated, such as the
/// leading-zero package-version aliases accepted by
/// <see cref="Instances.InstanceDataVersionComparer.FindBestMatch"/>. Because invalidation is complete,
/// <see cref="ResolutionTtlSeconds"/> is garbage collection rather than a correctness backstop, and
/// there is no recurring database load to keep resolutions fresh.
/// <para>
/// Determinism is unaffected by any of this: a running instance is pinned to its
/// <c>Instance.FlowVersion</c>, which is a full version, so it reads an immutable body. Only component
/// references that were <i>authored</i> with a range version (for example <c>view.version: "1"</c>)
/// resolve dynamically, and they would do so with or without a cache.
/// </para>
/// </remarks>
public sealed class ComponentCacheOptions
{
    /// <summary>
    /// Configuration section name for component cache options.
    /// </summary>
    public const string SectionName = "ComponentCache";

    /// <summary>
    /// Gets or sets the TTL in seconds for immutable full-version bodies. Default is 1800.
    /// Purely a memory bound — the content behind a full version never changes.
    /// </summary>
    [Range(60, 86400)]
    public int FullVersionTtlSeconds { get; set; } = 1800;

    /// <summary>
    /// Gets or sets the TTL in seconds for a component's generation token. Default is 3600.
    /// </summary>
    /// <remarks>
    /// Any token value is valid; only its <i>change</i> on publish carries meaning. This TTL exists
    /// solely as a last-resort backstop for the one failure mode that can reintroduce staleness: a
    /// publish whose token write fails after the body was written. Expiry forces a fresh token, which
    /// re-resolves everything. Lowering it buys a tighter staleness ceiling at the cost of one
    /// version-list load per component per interval.
    /// </remarks>
    [Range(60, 86400)]
    public int GenerationTtlSeconds { get; set; } = 3600;

    /// <summary>
    /// Gets or sets the TTL in seconds for resolution entries. Default is 3600.
    /// </summary>
    /// <remarks>
    /// This is garbage collection, not correctness: a generation bump already makes stale entries
    /// unreachable, and this bound just stops orphaned entries from older generations occupying memory
    /// indefinitely. It is safe to raise; raising it does not extend how long stale data can be served.
    /// </remarks>
    [Range(60, 86400)]
    public int ResolutionTtlSeconds { get; set; } = 3600;

    /// <summary>
    /// Gets or sets the TTL in seconds for negative (no matching version) resolution entries.
    /// Default is 30.
    /// </summary>
    /// <remarks>
    /// Short-lived on purpose: it exists so a bad or not-yet-published reference under load cannot
    /// repeatedly trigger a full version-list load, while still letting a genuinely new publish be
    /// picked up promptly. A generation bump clears negative entries as well, so a publish that
    /// satisfies the request takes effect immediately rather than after this interval.
    /// </remarks>
    [Range(1, 300)]
    public int NegativeTtlSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets how long, in seconds, a generation token may be memoized in process.
    /// Default is 0 (disabled).
    /// </summary>
    /// <remarks>
    /// Resolving a range version costs one extra distributed-cache read to fetch the generation token.
    /// Setting this above zero removes that read for the configured interval, at the cost of serving a
    /// stale resolution for up to that long after a publish. It is off by default because correctness
    /// comes first and because in-process state is otherwise avoided in this codebase for cache data —
    /// enable it deliberately, and only if the extra read is shown to matter.
    /// </remarks>
    [Range(0, 60)]
    public int GenerationMemoSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets whether publishing also deletes cache keys written by the pre-generation key
    /// layout. Default is true.
    /// </summary>
    /// <remarks>
    /// The previous layout wrote <c>:latest</c> and <c>:artifact:{version}</c> keys with no expiration.
    /// Nothing reads them any more, but they would otherwise linger, and during a rolling deployment
    /// pods still running the old build would keep serving them. Remove this option once no such pod
    /// can exist.
    /// </remarks>
    public bool PurgeLegacyKeysOnPublish { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the in-process (L1) envelope cache in front of the distributed cache is
    /// enabled. Default is true.
    /// </summary>
    /// <remarks>
    /// Correctness is carried by the key scheme, not by this flag: full-version bodies are immutable,
    /// and resolution entries embed the generation token in their key, so a publish bump makes stale
    /// L1 entries unreachable exactly as it does for L2. Disabling this restores the previous
    /// behavior of one distributed-cache read per envelope access.
    /// </remarks>
    public bool L1Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the memory budget in megabytes for the L1 envelope cache, shared across all
    /// component types in the process. Default is 64.
    /// </summary>
    /// <remarks>
    /// Entries are stored as serialized bytes and sized by their byte length, so this bounds actual
    /// payload memory. When the budget is exceeded, least-recently-used entries are compacted away —
    /// an eviction is a re-fetch from the distributed cache, never an error.
    /// </remarks>
    [Range(8, 2048)]
    public int L1SizeLimitMb { get; set; } = 64;
}
