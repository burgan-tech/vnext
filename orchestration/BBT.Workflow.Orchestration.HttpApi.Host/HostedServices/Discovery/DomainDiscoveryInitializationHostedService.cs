using System.Security.Cryptography;
using System.Text;
using BBT.Aether.DistributedLock;
using BBT.Workflow.Discovery;
using BBT.Workflow.Logging;

namespace BBT.Workflow.HostedServices;

/// <summary>
/// Background service that registers the current domain with service discovery at startup.
/// Any failure during domain discovery initialization is considered critical and will abort application startup.
/// <para>
/// Registration is service-level, not pod-level: the registered <c>baseUrl</c>/<c>healthUrl</c>
/// come from configuration and are identical on every replica, and registering starts a workflow
/// instance in the registry domain. With N replicas rolling out together, one healthy registration
/// is enough — the rest would just be redundant instance starts. A non-blocking, non-retrying
/// distributed lock (<see cref="RunAsync"/>) picks the single replica that performs it.
/// </para>
/// </summary>
public sealed class DomainDiscoveryInitializationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<DomainDiscoveryInitializationHostedService> logger) : BackgroundService
{
    /// <summary>
    /// Lease length, in seconds, for the once-per-rollout registration lock. Must comfortably
    /// exceed a rolling restart — replicas in the same rollout start seconds-to-minutes apart, not
    /// concurrently — while staying well below the redeploy cadence, so a genuine config change
    /// (a new <c>vNextApi:BaseUrl</c>) is not stuck behind a stale multi-hour lease. The lock key
    /// also carries the registered content (see <see cref="BuildLockKey"/>), so a changed URL gets
    /// a fresh key regardless of this value; this constant only bounds how long an *unchanged*
    /// redeploy stays skipped.
    /// </summary>
    internal const int RegistrationLeaseSeconds = 300;

    private const string LockKeyPrefix = "discovery:register";

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    /// <summary>
    /// Runs the guarded, once-per-rollout domain registration. Extracted out of
    /// <see cref="ExecuteAsync"/> so tests can invoke and await it directly instead of going
    /// through <see cref="BackgroundService.StartAsync"/>, whose <c>ExecuteAsync</c> is not
    /// guaranteed to run synchronously on the calling thread even when every awaited call
    /// completes immediately — which made assertions against it racy in practice.
    /// </summary>
    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Starting domain discovery initialization...");

            await using var scope = scopeFactory.CreateAsyncScope();
            var registrationService = scope.ServiceProvider.GetRequiredService<IDomainRegistrationService>();
            var lockService = scope.ServiceProvider.GetRequiredService<IDistributedLockService>();

            // The key is derived from what will actually be registered (see BuildLockKey), never
            // recomputed independently, so it can never drift from the real registration content.
            var identity = registrationService.GetRegistrationIdentity();
            var lockKey = BuildLockKey(identity);

            // Single attempt by design: a replica that does not get the lock must not wait and
            // must not retry. Another replica already owns (or will own) this rollout's
            // registration, and this pod starts normally either way.
            var handle = await lockService.TryAcquireLockAsync(lockKey, RegistrationLeaseSeconds, stoppingToken);
            if (handle is null)
            {
                logger.DomainRegistrationSkippedNotLockOwner(identity.DomainName);
                return;
            }

            logger.DomainRegistrationClaimed(identity.DomainName);

            try
            {
                await registrationService.RegisterDomainAsync(stoppingToken);
            }
            catch
            {
                // Hand the window back so another replica can try immediately; this pod then
                // aborts startup as before (outer catch) and is restarted.
                await handle.ReleaseAsync(CancellationToken.None);
                throw;
            }

            // Deliberately NOT released here. The lease is a once-per-window guard, not a mutex:
            // replicas in this rollout start seconds-to-minutes apart, not simultaneously, so
            // releasing on success would let the very next one see a free lock and re-register,
            // defeating the guard entirely. The lease is left to expire on its own.

            logger.LogInformation("Domain discovery initialization completed successfully");
        }
        catch (Exception ex)
        {
            // All domain discovery initialization failures are critical and abort application startup
            logger.LogCritical(ex, "Domain discovery initialization failed. Application startup will be aborted.");
            throw;
        }
    }

    /// <summary>
    /// Builds the once-per-rollout lock key from the registration identity: the domain name plus a
    /// short hash of <c>baseUrl + healthUrl</c>. Carrying the content (not just the domain name) in
    /// the key means a hotfix redeploy that changes the registered URL inside the previous lease
    /// window gets a fresh key and registers immediately, while a redeploy that changes nothing
    /// correctly finds the lease still held and skips.
    /// </summary>
    private static string BuildLockKey(DomainRegistrationIdentity identity)
    {
        var contentHash = ShortHash(identity.BaseUrl + identity.HealthUrl);
        return $"{LockKeyPrefix}:{identity.DomainName}:{contentHash}";
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }
}
