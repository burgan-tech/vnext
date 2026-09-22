using BBT.Workflow.Discovery;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.HostedServices;

/// <summary>
/// Drives <see cref="IDiscoveryCacheRefresher"/> on a timer: once at startup and then once per tick,
/// with the refresher's own marker deciding which ticks actually read the registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Warm-up is simply tick #1.</b> There is no separate startup path, so startup and steady state
/// cannot drift apart — and a pod that starts while the Dapr sidecar is still coming up fails tick one
/// quietly and succeeds on the next.
/// </para>
/// <para>
/// <b>By default the loop runs until the cluster is filled, then exits.</b> With
/// <c>Cache:RefreshIntervalSeconds = 0</c> there is no periodic window: invalidation arrives as an
/// event (<c>definitions/publish/completed</c>, a forced refresh, a transport failure), so once a
/// refresh has succeeded — or another replica has already served the window — there is nothing for a
/// timer to discover. The short tick is therefore purely a RETRY cadence: a first attempt that fails
/// because the sidecar or the registry was not ready at boot is retried within a tick instead of
/// leaving the pod cold.
/// </para>
/// <para>
/// With a non-zero refresh interval the loop keeps ticking and the shared marker decides which ticks
/// read the registry, as before; a tick whose window is already served costs one cache read.
/// </para>
/// <para>
/// <b>A failure here never aborts startup.</b> That is the deliberate difference from
/// <see cref="DomainDiscoveryInitializationHostedService"/>, which rethrows because a domain that
/// failed to register is genuinely broken. An unwarmed cache is not: every lookup falls back to the
/// live registry, which is the behaviour with the cache switched off. Failing the pod for it would
/// turn an optimisation into an availability risk, so the two must not be merged.
/// </para>
/// <para>
/// <b>Registered only in this host.</b> Orchestration is the only host calling
/// <c>AddInfrastructureRuntimeServices()</c> and therefore the only one with discovery at all. Should
/// another host ever call it, that host would read the shared cache without refreshing it — correct,
/// because a miss falls back, but worth knowing before it looks mysterious.
/// </para>
/// <para>
/// <b>The DI registration decides whether this runs, not a second copy of its predicate.</b>
/// <c>IDiscoveryCacheRefresher</c> exists only when <c>AddDomainDiscovery</c> enabled the cache, and
/// that decision is <c>!isDaprProvider &amp;&amp; Cache:Enabled</c> — the dapr provider derives its
/// app-id from a convention and never reads the registry, so there is nothing to warm. This service
/// therefore <b>probes for the refresher and exits</b> when it is absent, exactly as
/// <c>UtilityController.RefreshDiscoveryCacheAsync</c> answers "disabled" for its nullable one.
/// Spelling the condition again here (<c>Enabled &amp;&amp; Cache.Enabled</c>, as it once did) made
/// the two drift: under <c>Provider = "dapr"</c> with the default <c>Cache:Enabled = true</c> the
/// loop started against a service that was never registered and logged an
/// <c>InvalidOperationException</c> on <b>every tick, forever</b> — measured at 132 occurrences in one
/// lab pod. An absent registration is a boot-time fact, so the probe runs once, before the timer.
/// </para>
/// </remarks>
public sealed class DiscoveryCacheRefreshHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    TimeProvider timeProvider,
    ILogger<DiscoveryCacheRefreshHostedService> logger) : BackgroundService
{
    /// <summary>
    /// Upper bound of the random delay added to each tick, so replicas that started together do not
    /// contend on the same instant forever.
    /// </summary>
    private const int TickJitterSeconds = 5;

    private DiscoveryCacheOptions Cache => serviceDiscoveryOptions.Value.Cache;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = serviceDiscoveryOptions.Value;

        // Gated on both: a discovery-disabled deployment must not start polling a registry it was
        // explicitly told not to use.
        if (!options.Enabled || !Cache.Enabled)
            return;

        // Presence, not configuration: whatever reason the refresher was not registered for, there
        // is nothing for this loop to drive and no tick will ever change that.
        if (!IsRefresherRegistered())
        {
            logger.DiscoveryCacheRefresherNotRegistered(nameof(IDiscoveryCacheRefresher));
            return;
        }

        // Expiry-free mode (RefreshIntervalSeconds = 0): the loop's only job is to get the cluster
        // filled ONCE. Afterwards invalidation arrives as an event — publish-completed, a forced
        // refresh, a transport failure — so a pod that kept ticking would spend a marker read a
        // minute, forever, to be told nothing changed.
        var warmUpOnly = Cache.RefreshIntervalSeconds <= 0;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Cache.TickIntervalSeconds));

        do
        {
            var outcome = await TickAsync(stoppingToken);

            if (warmUpOnly && IsClusterFilled(outcome))
            {
                logger.DiscoveryCacheWarmUpCompleted(outcome!.Value.ToString());
                return;
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>
    /// Whether the cluster's cache is now filled, so a warm-up-only loop has nothing left to do.
    /// </summary>
    /// <remarks>
    /// <see cref="DiscoveryCacheRefreshOutcome.SkippedNotOwner"/> is deliberately NOT included:
    /// another replica holding the lock has not finished yet, and if its read fails nobody writes
    /// the marker. Ticking once more costs one cache read and turns into
    /// <see cref="DiscoveryCacheRefreshOutcome.SkippedWindowFresh"/> the moment that replica
    /// succeeds — whereas exiting here would leave this pod resolving live for the rest of its life
    /// if the other one failed.
    /// </remarks>
    private static bool IsClusterFilled(DiscoveryCacheRefreshOutcome? outcome)
        => outcome is DiscoveryCacheRefreshOutcome.Refreshed
            or DiscoveryCacheRefreshOutcome.SkippedWindowFresh;

    /// <summary>
    /// Whether the container holds an <see cref="IDiscoveryCacheRefresher"/> at all.
    /// </summary>
    /// <remarks>
    /// Asks <see cref="IServiceProviderIsService"/> rather than resolving, deliberately: resolving
    /// would ACTIVATE the singleton, and a constructor that threw here would escape
    /// <c>ExecuteAsync</c> and — under the default <c>BackgroundServiceExceptionBehavior.StopHost</c>
    /// — take the pod down, breaking this service's one hard promise that an unwarmed cache never
    /// aborts startup. A container that does not offer the probe is treated as "present", which
    /// degrades to the previous behaviour (the loop runs and each tick's own catch handles it)
    /// instead of silently disabling the refresh.
    /// </remarks>
    internal bool IsRefresherRegistered()
    {
        using var scope = scopeFactory.CreateScope();
        var probe = scope.ServiceProvider.GetService<IServiceProviderIsService>();

        return probe is null || probe.IsService(typeof(IDiscoveryCacheRefresher));
    }

    /// <summary>
    /// One scheduled refresh attempt. Never throws.
    /// </summary>
    /// <returns>
    /// What the attempt did, or <c>null</c> when it could not run at all (shutdown, or a failure
    /// building the scope). A null is never treated as "filled".
    /// </returns>
    internal async Task<DiscoveryCacheRefreshOutcome?> TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var refresher = scope.ServiceProvider.GetRequiredService<IDiscoveryCacheRefresher>();

            return await refresher.RefreshAsync(force: false, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
            return null;
        }
        catch (Exception ex)
        {
            // The refresher already swallows its own failures; this catches anything that goes wrong
            // building the scope, so the timer loop survives it.
            logger.LogWarning(ex, "Discovery cache refresh tick failed");
            return null;
        }
    }

    /// <summary>
    /// Waits for the next tick plus a small jitter, returning false on shutdown.
    /// </summary>
    private async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            if (!await timer.WaitForNextTickAsync(stoppingToken))
                return false;

            var jitter = Random.Shared.Next(0, TickJitterSeconds * 1000);
            await Task.Delay(TimeSpan.FromMilliseconds(jitter), timeProvider, stoppingToken);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
