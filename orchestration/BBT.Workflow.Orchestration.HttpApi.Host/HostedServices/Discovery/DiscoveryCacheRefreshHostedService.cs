using BBT.Workflow.Discovery;
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
/// <b>Ticking far more often than the refresh window is intentional and nearly free.</b> A tick whose
/// window is already served costs one cache read and stops there. What the short tick buys is prompt
/// recovery: a failed window — sidecar not ready at boot, a registry blip, a replica that died holding
/// the lease — is retried within a tick rather than at the end of an hour-long window.
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

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Cache.TickIntervalSeconds));

        do
        {
            await TickAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>
    /// One scheduled refresh attempt. Never throws.
    /// </summary>
    internal async Task TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var refresher = scope.ServiceProvider.GetRequiredService<IDiscoveryCacheRefresher>();

            await refresher.RefreshAsync(force: false, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // The refresher already swallows its own failures; this catches anything that goes wrong
            // building the scope, so the timer loop survives it.
            logger.LogWarning(ex, "Discovery cache refresh tick failed");
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
