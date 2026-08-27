namespace BBT.Workflow.Discovery;

/// <summary>
/// The identity of a domain registration: the exact domain name, base URL and health URL that
/// <see cref="IDomainRegistrationService.RegisterDomainAsync"/> sends to the registry.
/// Exposed so callers that need to key on "what would be registered" (e.g. a distributed-lock key
/// guarding once-per-rollout registration) can never drift from what registration actually posts.
/// </summary>
/// <param name="DomainName">The domain name this runtime instance serves.</param>
/// <param name="BaseUrl">
/// The configured <c>vNextApi:BaseUrl</c>, trimmed of a trailing slash. Empty when not configured
/// (e.g. service discovery disabled and the setting was never set).
/// </param>
/// <param name="HealthUrl">
/// <c>{BaseUrl}/health</c>, or empty when <paramref name="BaseUrl"/> is empty.
/// </param>
/// <param name="Enabled">
/// The current value of <c>ServiceDiscovery:Enabled</c>. Surfaced here — rather than read
/// separately from options by callers — so a once-per-rollout registration guard (which must skip
/// the lock entirely when discovery is disabled, not just skip the HTTP call) can never disagree
/// with what <see cref="IDomainRegistrationService.RegisterDomainAsync"/> itself will do.
/// </param>
public readonly record struct DomainRegistrationIdentity(string DomainName, string BaseUrl, string HealthUrl, bool Enabled);

/// <summary>
/// Service responsible for registering the current domain with the service registry.
/// This service triggers the domain-registration workflow to register the domain
/// and enable health check scheduling.
/// </summary>
public interface IDomainRegistrationService
{
    /// <summary>
    /// Registers the current domain with the service registry by starting the domain-registration workflow.
    /// This method sends domain information including domain name, base URL, and health URL
    /// to the registry for service discovery purposes.
    /// </summary>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous registration operation.</returns>
    Task RegisterDomainAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the identity that <see cref="RegisterDomainAsync"/> would register — domain name,
    /// base URL and health URL — without performing any I/O or validation. Callers that need to
    /// derive a key from "what will be registered" (e.g. a once-per-rollout distributed lock key)
    /// should use this instead of recomputing the same values, so the key can never drift from
    /// what actually gets registered.
    /// </summary>
    DomainRegistrationIdentity GetRegistrationIdentity();
}