using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Validates the cross-field relationships in <see cref="DiscoveryCacheOptions"/>, which
/// <c>[Range]</c> annotations cannot express.
/// </summary>
/// <remarks>
/// Every rule here exists because breaking it degrades the cache <i>silently</i> — no exception, no
/// error log, just a cache that quietly stops paying for itself or a staleness window nobody
/// intended. Failing at startup is the only place these are visible.
/// <para>
/// Lives on the parent options type because <c>Cache</c> is a nested property: the options system
/// validates the type it binds, and it binds <see cref="ServiceDiscoveryOptions"/>.
/// </para>
/// </remarks>
public sealed class ServiceDiscoveryOptionsValidator : IValidateOptions<ServiceDiscoveryOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ServiceDiscoveryOptions options)
    {
        var cache = options.Cache;

        if (!cache.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();

        // L1 is the layer the refresher cannot reach. Longer than a refresh window and a pod keeps
        // serving an address the cluster has already corrected.
        if (cache.L1TtlSeconds >= cache.RefreshIntervalSeconds)
        {
            failures.Add(
                $"ServiceDiscovery:Cache:L1TtlSeconds ({cache.L1TtlSeconds}) must be less than " +
                $"RefreshIntervalSeconds ({cache.RefreshIntervalSeconds}); otherwise a pod can serve a " +
                "stale endpoint for longer than the cluster takes to correct it.");
        }

        // A tick slower than the refresh window means a failed refresh is not retried within it.
        if (cache.TickIntervalSeconds > cache.RefreshIntervalSeconds)
        {
            failures.Add(
                $"ServiceDiscovery:Cache:TickIntervalSeconds ({cache.TickIntervalSeconds}) must not exceed " +
                $"RefreshIntervalSeconds ({cache.RefreshIntervalSeconds}); a failed refresh would not be " +
                "retried inside the window it failed in.");
        }

        // The one that makes the cache useless without saying so: entries expire between refreshes,
        // so every domain's first caller after each expiry pays full registry latency.
        if (cache.RefreshIntervalSeconds >= cache.L2TtlSeconds)
        {
            failures.Add(
                $"ServiceDiscovery:Cache:RefreshIntervalSeconds ({cache.RefreshIntervalSeconds}) must be less " +
                $"than L2TtlSeconds ({cache.L2TtlSeconds}); otherwise entries expire between refreshes and the " +
                "cache silently stops serving.");
        }

        // The lease must fit inside a window, or one refresh can still hold the lock when the next
        // is due.
        if (cache.WarmupLockLeaseSeconds >= cache.RefreshIntervalSeconds)
        {
            failures.Add(
                $"ServiceDiscovery:Cache:WarmupLockLeaseSeconds ({cache.WarmupLockLeaseSeconds}) must be less " +
                $"than RefreshIntervalSeconds ({cache.RefreshIntervalSeconds}).");
        }

        // Survive one lost window without falling back to live lookups for everything.
        var minimumL2Ttl = cache.RefreshIntervalSeconds + cache.WarmupLockLeaseSeconds;
        if (cache.L2TtlSeconds < minimumL2Ttl)
        {
            failures.Add(
                $"ServiceDiscovery:Cache:L2TtlSeconds ({cache.L2TtlSeconds}) must be at least " +
                $"RefreshIntervalSeconds + WarmupLockLeaseSeconds ({minimumL2Ttl}) so a single missed refresh " +
                "window does not expire the whole cache.");
        }

        if (cache.AcceptedStatuses.Count == 0)
        {
            failures.Add(
                "ServiceDiscovery:Cache:AcceptedStatuses must not be empty; an empty set discards every " +
                "registration and warms nothing.");
        }

        if (string.IsNullOrWhiteSpace(cache.BulkEndpointTemplate))
            failures.Add("ServiceDiscovery:Cache:BulkEndpointTemplate must not be empty.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
