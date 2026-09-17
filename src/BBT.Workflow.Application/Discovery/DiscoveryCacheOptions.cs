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
/// configure makes no network call on a hit, and pays for the staleness with the bounded window
/// below. Do not widen that window casually — it is the entire reason this is allowed to exist.
/// </para>
/// <para>
/// <b>Staleness budget.</b> A domain whose <c>baseUrl</c> moves is routed to the old address for at
/// most <c>RefreshIntervalSeconds + fetch + L1TtlSeconds</c> — about seventy minutes with these
/// defaults,
/// which is a deliberate choice: a domain's registered address changes on the order of deployments,
/// not minutes, so the window is sized to how often the data actually moves rather than to how
/// quickly it could. The hour is safe only because it is not the only mitigation —
/// <c>POST utilities/discovery/refresh</c> re-reads the registry synchronously, so the one event
/// that invalidates an entry early (a domain moving) has an operator-triggered answer that does not
/// wait for the window.
/// </para>
/// <para>
/// If the refresher stops entirely, entries age out at <see cref="L2TtlSeconds"/> and every lookup
/// falls back to a live registry call — the cache fails <i>open</i>, into the pre-cache behaviour,
/// never into a stale answer.
/// </para>
/// <para>
/// A window this long also makes the cache a <i>resilience</i> feature and not only a latency one:
/// with entries valid for two hours, cross-domain traffic survives a discovery-registry outage
/// instead of failing with it.
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
    /// How long an entry may be served from the in-process layer, in seconds. Default 600.
    /// </summary>
    /// <remarks>
    /// L1 is the only layer the refresher cannot overwrite, so it is the one that lets two pods
    /// disagree about where a domain lives — and this value is exactly how long that disagreement can
    /// last. It is also, and more importantly, <b>the residual staleness after a forced refresh</b>:
    /// <c>POST utilities/discovery/refresh</c> corrects the shared layer immediately, but every pod
    /// except the one that ran it keeps serving its own copy until this expires. That is the number
    /// to look at when sizing this, not the L2 read it saves — the saving is one cache read per
    /// domain per expiry, which is negligible at any value in this range.
    /// <para>
    /// Ten minutes is a deliberate trade against the 60 s this shipped with: a domain's address moves
    /// on the order of deployments, so a ten-minute disagreement is acceptable, but it does mean the
    /// forced-refresh endpoint now takes up to ten minutes to take effect fleet-wide rather than one.
    /// Keep it well under <see cref="RefreshIntervalSeconds"/> (the validator enforces it): at parity
    /// a pod could serve a stale endpoint for twice the window the cluster takes to correct it.
    /// </para>
    /// </remarks>
    [Range(1, 3600)]
    public int L1TtlSeconds { get; set; } = 600;

    /// <summary>
    /// How often each pod wakes up to consider refreshing, in seconds. Default 60.
    /// </summary>
    /// <remarks>
    /// Not the refresh rate — <see cref="RefreshIntervalSeconds"/> is, and the shared marker enforces
    /// it. A tick whose window is already served costs one cache read and stops, which is why this
    /// stays short even though the window is an hour: what it buys is prompt recovery. A failed
    /// window — sidecar not ready at boot, a registry blip, a replica that died holding the lease —
    /// is retried within a tick instead of at the end of the hour.
    /// </remarks>
    [Range(1, 3600)]
    public int TickIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Minimum interval between two cluster-wide bulk refreshes, in seconds. Default 3600 (1 hour).
    /// </summary>
    /// <remarks>
    /// The dominant term in the staleness budget, and sized to the data: a domain's registered
    /// address changes when that domain is deployed to a new address, which is a rare, planned event.
    /// Enforced by a shared marker rather than by each pod's timer, so N replicas produce one bulk
    /// read per window, not N.
    /// </remarks>
    [Range(5, 86400)]
    public int RefreshIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum age of a cached registration before it is treated as absent, in seconds.
    /// Default 7200 (2 hours).
    /// </summary>
    /// <remarks>
    /// This is the dead-man's switch, not a performance knob: it is what bounds staleness when the
    /// refresher is not running at all. Twice <see cref="RefreshIntervalSeconds"/> by default, which
    /// is the principled setting — survive one missed window, expire after roughly two — and it is
    /// what makes the cache degrade to the pre-cache behaviour rather than to a stale answer.
    /// Validated on read against the recorded fetch time rather than being left to the distributed
    /// store's own expiry — see <c>CachingDiscoveryRegistryClient</c> for why that distinction is
    /// load-bearing.
    /// </remarks>
    [Range(5, 604800)]
    public int L2TtlSeconds { get; set; } = 7200;

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
}
