using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Settings for the registry-read cache that sits in front of
/// <c>IDiscoveryRegistryClient</c> (configuration section <c>ServiceDiscovery:Cache</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>This cache applies to the default (<c>http</c>) provider only.</b> The Dapr provider derives
/// app-ids from a naming convention and makes no network call at all on its default path, so it has
/// nothing to gain here; it keeps its own short-lived in-process app-id cache as the single cache on
/// that path. The scope is enforced at REGISTRATION (see <c>AddDomainDiscovery</c>), not by a branch
/// inside the decorator, so the Dapr path is genuinely untouched rather than merely bypassed.
/// </para>
/// <para>
/// <b>A cache here was shipped once and deliberately removed</b> (commit <c>79da3b6f</c>, "the bulk
/// domain cache's staleness risk … is not worth its latency saving"). That verdict was correct for
/// that implementation, which revalidated over HTTP on every cache hit and therefore paid the full
/// registry latency anyway while carrying the staleness of a cache. The design these options
/// configure makes no network call on a hit, and answers that objection the other way round: not
/// with a window short enough to be defensible, but by removing the window and invalidating on the
/// event that actually changes the data.
/// </para>
/// <para>
/// <b>Invalidation is an EVENT, not a clock.</b> With the defaults below there is no expiry at all:
/// a pod fills the cache once at startup and the entries stay until something says otherwise. The
/// something is <c>POST definitions/publish/completed</c>, which a domain's CD pipeline calls once
/// after it has published every component of a package, and which forces a full re-read of the
/// registry. The reasoning is that the cached datum — where a domain answers — changes when that
/// domain is deployed, not continuously, so a timer can only either fire when nothing changed or
/// fire too late. What it cost before was a ~70 minute window during which a moved domain was
/// routed to an address it no longer answered on.
/// </para>
/// <para>
/// <b>What replaces the dead-man TTL is negative feedback, not a shorter timer.</b> A stale entry
/// does damage only while something tries to USE it, and that moment is observable: a transport
/// failure against a cached endpoint evicts it (see <see cref="UnreachableEvictionCooldownSeconds"/>)
/// and the next resolution goes live. That covers the one case the event cannot — a peer domain
/// moving while this domain has no deployment of its own — without reintroducing periodic work.
/// </para>
/// <para>
/// <b>The operator escape hatch stays.</b> <c>POST utilities/discovery/refresh</c> re-reads the
/// registry synchronously and is the answer to a wrongly registered domain; with no TTL behind it,
/// it is a real mechanism rather than a way to shave minutes off a window.
/// </para>
/// <para>
/// A cache that never expires is also a <i>resilience</i> feature: cross-domain traffic survives a
/// discovery-registry outage of any length instead of degrading with it.
/// </para>
/// </remarks>
public sealed class DiscoveryCacheOptions
{
    /// <summary>
    /// Configuration section name, relative to <see cref="ServiceDiscoveryOptions.SectionName"/>.
    /// </summary>
    public const string SectionName = "Cache";

    /// <summary>
    /// Master switch. Default <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Off by default on purpose. This reverses a shipped decision, and domain teams consuming the
    /// runtime as a package inherit code defaults — turning it on here would silently reintroduce a
    /// staleness window for every one of them on upgrade. It is enabled per deployment through the
    /// host's <c>appsettings.json</c>, which operators already own and can revert.
    /// <para>
    /// Setting it to <c>false</c> restores the pre-cache behaviour <i>literally</i>: the plain
    /// registry client is registered and the refresher is never started, so not even a stale entry
    /// written before the flip remains readable.
    /// </para>
    /// </remarks>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether the in-process (L1) layer is used in front of the distributed (L2) layer.
    /// Default <c>true</c>.
    /// </summary>
    public bool L1Enabled { get; set; } = true;

    /// <summary>
    /// How long an entry may be served from the in-process layer, in seconds. Default 60.
    /// </summary>
    /// <remarks>
    /// L1 is the only layer a refresh cannot overwrite, so this value is exactly two things: how
    /// long two pods can disagree about where a domain lives, and <b>how long a refresh takes to
    /// reach the pods that did not perform it</b>. The refresher and the publish-completed hook both
    /// write the shared layer; every other pod keeps its own copy until this expires.
    /// <para>
    /// Sixty seconds, not the ten minutes this carried while there was an hour-long refresh window
    /// to stay under. With invalidation driven by a deployment rather than a timer, the only thing
    /// a longer L1 buys is one saved distributed-cache read per domain per expiry — negligible —
    /// while the thing it costs is the propagation delay of every invalidation. That trade only
    /// pointed one way once the window was gone.
    /// </para>
    /// <para>
    /// Must stay above zero: L1 is what keeps a hot cross-domain path off the distributed cache
    /// entirely, and disabling it belongs in <see cref="L1Enabled"/>, where it is visible.
    /// </para>
    /// </remarks>
    [Range(1, 3600)]
    public int L1TtlSeconds { get; set; } = 60;

    /// <summary>
    /// How often a pod wakes up to consider refreshing, in seconds. Default 60.
    /// </summary>
    /// <remarks>
    /// Not the refresh rate. In the default, expiry-free mode the loop exists only to get the cache
    /// filled ONCE — it stops as soon as a refresh has succeeded or another replica has claimed the
    /// window — so this value is how quickly a failed first attempt is retried, nothing more. That
    /// retry is the reason it stays short: a pod whose Dapr sidecar or registry was not ready at boot
    /// must not wait out a long interval with a cold cache.
    /// <para>
    /// When <see cref="RefreshIntervalSeconds"/> is non-zero the loop keeps ticking and this is again
    /// the retry cadence inside that window, not the window itself.
    /// </para>
    /// </remarks>
    [Range(1, 3600)]
    public int TickIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Interval between two cluster-wide periodic bulk refreshes, in seconds.
    /// <b><c>0</c> (the default) disables periodic refreshing entirely.</b>
    /// </summary>
    /// <remarks>
    /// At <c>0</c> the cache is filled once per cluster — warm-up — and afterwards only by an event:
    /// <c>definitions/publish/completed</c> at the end of a deployment, the operator's
    /// <c>utilities/discovery/refresh</c>, or a miss. The refresh marker is then written without an
    /// expiry, because it no longer names a window; it records that the cluster has been filled.
    /// <para>
    /// Setting it back to a positive value restores the periodic behaviour, marker window and all.
    /// It is a rollback lever, not a tuning knob: the reason to reach for it is a deployment whose
    /// CD pipeline cannot be relied on to call publish-completed, and in that case
    /// <see cref="L2TtlSeconds"/> should be set alongside it — a periodic refresh with no expiry
    /// behind it still cannot notice that a domain has disappeared.
    /// </para>
    /// <para>
    /// Enforced cluster-wide by the shared marker rather than by each pod's timer, so N replicas
    /// produce one bulk read per window, not N.
    /// </para>
    /// </remarks>
    [Range(0, 86400)]
    public int RefreshIntervalSeconds { get; set; } = 0;

    /// <summary>
    /// Maximum age of a cached registration before it is treated as absent, in seconds.
    /// <b><c>0</c> (the default) means entries never expire.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// At <c>0</c> no expiry is written to the distributed store and no age ceiling is applied on
    /// read: an entry lives until an event replaces or evicts it. That is the deliberate trade of
    /// this design — a dead-man switch can only convert "wrong address" into "wrong address for a
    /// while", and the case it was guarding (a peer domain moving with no deployment here) is now
    /// handled at the moment it actually hurts, by
    /// <see cref="UnreachableEvictionCooldownSeconds"/>'s transport-failure eviction.
    /// </para>
    /// <para>
    /// The age of the entry that answered is still recorded on every resolution
    /// (<c>vnext.discovery.cache.age_seconds</c>), and with no TTL that tag carries MORE weight, not
    /// less: it is the only way to notice a three-week-old entry, and the only way to answer "was
    /// this routed by a stale entry?" during an incident.
    /// </para>
    /// <para>
    /// A positive value restores the previous dead-man behaviour: entries older than this are
    /// treated as a miss and resolved live. Validated on read against the recorded fetch time rather
    /// than left to the distributed store's own expiry — see <c>CachingDiscoveryRegistryClient</c>
    /// for why that distinction is load-bearing. Pair it with a non-zero
    /// <see cref="RefreshIntervalSeconds"/>, or every entry starts paying a live lookup once per
    /// expiry.
    /// </para>
    /// </remarks>
    [Range(0, 604800)]
    public int L2TtlSeconds { get; set; } = 0;

    /// <summary>
    /// Lease length for the bulk-refresh lock, in seconds. Default 30.
    /// </summary>
    /// <remarks>
    /// The lease does NOT auto-renew, so the bulk fetch is bounded slightly under this value; a
    /// fetch that outran the lease would otherwise keep writing after another pod had taken over.
    /// </remarks>
    [Range(5, 3600)]
    public int WarmupLockLeaseSeconds { get; set; } = 30;

    /// <summary>
    /// Relative URL template for the bulk read, appended to
    /// <see cref="ServiceDiscoveryOptions.BaseUrl"/>. <c>{0}</c> = registry domain.
    /// </summary>
    /// <remarks>
    /// Points at the registry's <c>domain-list</c> function, which is Domain-scope and answers
    /// <c>{ "items": [ { domainName, baseUrl, appId, healthUrl } ] }</c> for the whole registry in
    /// one request. The registry owns the projection — it filters to active registrations, orders
    /// them, resolves the domain name from the instance key and drops anything unroutable — so there
    /// is nothing left to re-derive here and no pagination to drive.
    /// <para>
    /// <b>The registry must actually carry that function.</b> A discovery deployment whose package
    /// predates it answers 404, every refresh window fails, and the cache simply stays cold: lookups
    /// fall back to the live per-domain path, which is the behaviour with the cache switched off.
    /// The template is configurable so an API-gateway path variation stays a config change.
    /// </para>
    /// </remarks>
    public string DomainListEndpointTemplate { get; set; } = "/{0}/functions/domain-list";

    /// <summary>
    /// Number of listed domains at which the list is assumed to be truncated. Default 500.
    /// </summary>
    /// <remarks>
    /// The registry function serves a single page and its response carries no truncation signal, so
    /// a full page is the only evidence that domains may be missing. Reaching this count is logged at
    /// Warning and the list is published anyway: a partial warm-up still serves the domains it holds,
    /// and the ones it does not simply pay a live lookup — where refusing to publish would leave the
    /// cache empty and every domain paying it. Keep it aligned with the registry function's own page
    /// size; lowering it only makes the warning fire earlier.
    /// </remarks>
    [Range(1, 100000)]
    public int DomainListExpectedMax { get; set; } = 500;

    /// <summary>
    /// Minimum interval between two evictions of the SAME domain triggered by a transport failure,
    /// in seconds. Default 30. <c>0</c> disables failure-driven eviction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failure-driven eviction is what makes an expiry-free cache safe: a cached endpoint that no
    /// longer answers is dropped the first time something fails to reach it, and the next resolution
    /// goes live. Without the cooldown, though, it would turn a peer domain's OUTAGE into a load
    /// problem of our own — a correctly addressed but down domain fails every call, and every
    /// failure would evict and force a fresh registry lookup. The cooldown makes that one lookup per
    /// interval instead of one per failed call.
    /// </para>
    /// <para>
    /// Only transport-level failures evict — connection refused, DNS failure, unreachable host. An
    /// HTTP 4xx or 5xx never does: a domain answering with an error is a domain at the right
    /// address, and evicting on it would make every downstream bug look like a discovery problem.
    /// </para>
    /// </remarks>
    [Range(0, 3600)]
    public int UnreachableEvictionCooldownSeconds { get; set; } = 30;
}
