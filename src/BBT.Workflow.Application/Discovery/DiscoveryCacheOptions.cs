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
/// most <c>RefreshIntervalSeconds + fetch + L1TtlSeconds</c> — about an hour with these defaults,
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
    /// How long an entry may be served from the in-process layer, in seconds. Default 60.
    /// </summary>
    /// <remarks>
    /// L1 is the only layer the refresher cannot overwrite, so it is the one that lets two pods
    /// disagree about where a domain lives — and this value is exactly how long that disagreement can
    /// last. It is also, and more importantly, the residual staleness after a forced refresh: the
    /// endpoint corrects the shared layer immediately, but each pod's own copy clears on its own
    /// schedule. Keep it short even though the refresh window is long; a minute is already noise
    /// against an hour, and shrinking it further buys only faster incident recovery.
    /// </remarks>
    [Range(1, 3600)]
    public int L1TtlSeconds { get; set; } = 60;

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
    /// Page size for the bulk read. Default 100, which is also the registry API's maximum.
    /// </summary>
    [Range(1, 100)]
    public int BulkPageSize { get; set; } = 100;

    /// <summary>
    /// Hard cap on pages fetched in one refresh. Default 20 (2000 domains).
    /// </summary>
    /// <remarks>
    /// A runaway guard against a registry that ignores <c>page</c>. Hitting it is logged at Warning:
    /// silent truncation is how the previous implementation cached only the first page, forever,
    /// with nothing to show for it in the logs.
    /// </remarks>
    [Range(1, 1000)]
    public int MaxPages { get; set; } = 20;

    /// <summary>
    /// Relative URL template for the bulk read, appended to
    /// <see cref="ServiceDiscoveryOptions.BaseUrl"/>.
    /// <c>{0}</c> = registry domain, <c>{1}</c> = page, <c>{2}</c> = page size.
    /// </summary>
    public string BulkEndpointTemplate { get; set; } = "/{0}/workflows/domain/instances?page={1}&pageSize={2}";

    /// <summary>
    /// Server-side filter sent with the bulk read. Empty disables it.
    /// </summary>
    /// <remarks>
    /// An optimisation only. The client re-checks each item's status regardless, because older
    /// runtimes answer <c>400</c> for this filter shape and the refresher then retries unfiltered.
    /// </remarks>
    public string BulkFilter { get; set; } = "{\"status\":{\"eq\":\"A\"}}";

    /// <summary>
    /// Instance statuses accepted from the bulk read. Default <c>A</c> (Active).
    /// </summary>
    /// <remarks>
    /// This is the status of the <i>registration workflow instance</i>, not a health signal. A
    /// registration flow that reaches a Finish state becomes <c>C</c>, and requiring <c>A</c> would
    /// then drop a perfectly live domain from the warm-up — the lookup still works, but every call
    /// for it pays full registry latency. Configurable so a deployment whose registration flow
    /// completes can add <c>C</c> without a code change.
    /// </remarks>
    public HashSet<string> AcceptedStatuses { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "A" };
}
