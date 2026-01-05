namespace BBT.Workflow.Discovery;

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
}