using BBT.Aether.DistributedLock;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// What one refresh attempt did.
/// </summary>
public enum DiscoveryCacheRefreshOutcome
{
    /// <summary>The registry was read and every entry republished.</summary>
    Refreshed,

    /// <summary>Another replica had already served this window.</summary>
    SkippedWindowFresh,

    /// <summary>Another replica holds the refresh lock right now.</summary>
    SkippedNotOwner,

    /// <summary>The registry could not be read; existing entries were left alone.</summary>
    Failed
}

/// <summary>
/// Performs the cluster-wide bulk refresh of the discovery cache.
/// </summary>
public interface IDiscoveryCacheRefresher
{
    /// <summary>
    /// Refreshes the cache if this replica wins the window.
    /// </summary>
    /// <param name="force">
    /// Skips the window check, so an operator-triggered refresh runs immediately instead of waiting
    /// out the current window. Still takes the lock, so two simultaneous forced refreshes do not both
    /// read the registry.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>Never throws.</remarks>
    Task<DiscoveryCacheRefreshOutcome> RefreshAsync(bool force, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// The guard is deliberately two-part, and the halves do different jobs.
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>The marker defines the window.</b> Checked first, it means the ordinary attempt never
///     touches the lock at all — and it is what prevents a stampede when the lock is released.
///     <c>DomainDiscoveryInitializationHostedService</c> gets away with never releasing its lease
///     because for a once-per-rollout job the lease <i>is</i> the guard; a periodic refresh must be
///     able to re-acquire, and a released lock with no marker lets every replica acquire in turn and
///     each perform a full bulk read.
///   </description></item>
///   <item><description>
///     <b>The lock serializes the racers inside one window</b>, and the second marker check — inside
///     the lock — closes the gap between the first check and the acquire.
///   </description></item>
/// </list>
/// <para>
/// A replica that dies holding the lease stalls nothing permanently: the lease expires, no marker was
/// written, and the next attempt proceeds. The bound is lease + tick interval, never indefinite.
/// </para>
/// </remarks>
public sealed class DiscoveryCacheRefresher(
    IDiscoveryRegistryClient registryClient,
    IDiscoveryCacheWriter cacheWriter,
    IDistributedLockService lockService,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    ILogger<DiscoveryCacheRefresher> logger) : IDiscoveryCacheRefresher
{
    /// <summary>
    /// Lock guarding one bulk read. Distinct from the refresh marker, which guards the window.
    /// </summary>
    public const string RefreshLockKey = "discovery:bulk:v1:lock";

    /// <summary>
    /// Slack subtracted from the lease to bound the bulk fetch.
    /// </summary>
    /// <remarks>
    /// The distributed lock does NOT auto-renew, so a fetch slower than its lease would carry on
    /// writing after another replica had legitimately taken the window over. Bounding the fetch turns
    /// that into a failed window — no marker, previous entries left to expire on their own — instead
    /// of two replicas publishing over each other.
    /// </remarks>
    private const int LeaseSafetyMarginSeconds = 5;

    private DiscoveryCacheOptions Cache => serviceDiscoveryOptions.Value.Cache;

    /// <inheritdoc />
    public async Task<DiscoveryCacheRefreshOutcome> RefreshAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        try
        {
            // The common case: this window is already served, and the lock is never touched.
            if (!force && await cacheWriter.GetRefreshMarkerAsync(cancellationToken) is not null)
            {
                logger.BulkCacheRefreshSkippedFresh();
                return DiscoveryCacheRefreshOutcome.SkippedWindowFresh;
            }

            // Single attempt, no wait, no retry: another replica owning this window is a normal
            // outcome, not a contention to fight over.
            var handle = await lockService.TryAcquireLockAsync(
                RefreshLockKey, Cache.WarmupLockLeaseSeconds, cancellationToken);

            if (handle is null)
            {
                logger.BulkCacheRefreshSkippedNotOwner();
                return DiscoveryCacheRefreshOutcome.SkippedNotOwner;
            }

            try
            {
                // Re-check under the lock: between the first check and the acquire, another replica
                // may have finished. A forced refresh deliberately skips this too — the operator
                // asking for one already knows the window is "fresh"; that is why they are asking.
                if (!force && await cacheWriter.GetRefreshMarkerAsync(cancellationToken) is not null)
                {
                    logger.BulkCacheRefreshSkippedFresh();
                    return DiscoveryCacheRefreshOutcome.SkippedWindowFresh;
                }

                logger.BulkCacheRefreshStarted();

                // Bounded by the lease actually held.
                using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                fetchCts.CancelAfter(TimeSpan.FromSeconds(
                    Math.Max(1, Cache.WarmupLockLeaseSeconds - LeaseSafetyMarginSeconds)));

                var all = await registryClient.ListAllAsync(fetchCts.Token);

                if (!all.IsSuccess)
                {
                    logger.BulkCacheRefreshFailed(all.Error.Message ?? all.Error.Code);
                    return DiscoveryCacheRefreshOutcome.Failed;
                }

                // An empty result is a failed window, not "no domains exist". If the registry ever
                // applies caller-role filtering to its instance list, an under-authenticated refresher
                // receives 200 with zero items — and publishing that would record an empty cluster
                // with complete confidence.
                if (all.Value!.Count == 0)
                {
                    logger.BulkCacheRefreshFailed("the registry returned no registrations");
                    return DiscoveryCacheRefreshOutcome.Failed;
                }

                await cacheWriter.SetAsync(all.Value!, cancellationToken);
                await cacheWriter.SetRefreshMarkerAsync(cancellationToken);

                logger.BulkCacheRefreshed(all.Value!.Count);
                return DiscoveryCacheRefreshOutcome.Refreshed;
            }
            finally
            {
                // ALWAYS released. The window guard is the marker; holding the lease past the work
                // would only delay the next window's legitimate refresh.
                await handle.ReleaseAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DiscoveryCacheRefreshOutcome.Failed;
        }
        catch (Exception ex)
        {
            // Deliberately swallowed. An unwarmed cache degrades to the pre-cache behaviour — every
            // lookup falls back to the live registry — so it is never a reason to fail a caller or
            // take a pod down.
            logger.BulkCacheRefreshFailed(ex.Message);
            return DiscoveryCacheRefreshOutcome.Failed;
        }
    }
}
