using System.Reflection;
using BBT.Aether;
using BBT.Aether.DistributedCache;
using BBT.Workflow.Domain;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.ExceptionHandling;

namespace BBT.Workflow.Runtime;

/// <summary>
/// Provides access to runtime information for the workflow system.
/// This interface defines the contract for retrieving runtime version and domain information,
/// as well as validating domain access permissions.
/// </summary>
public interface IRuntimeInfoProvider
{
    /// <summary>
    /// Gets the current version of the workflow runtime system.
    /// </summary>
    /// <value>
    /// A string representing the runtime version, typically in semantic versioning format (e.g., "1.0.0").
    /// </value>
    string Version { get; }

    /// <summary>
    /// Gets the domain name that this runtime instance is configured to serve.
    /// </summary>
    /// <value>
    /// A string representing the domain name that defines the scope of this runtime instance.
    /// </value>
    string Domain { get; }

    /// <summary>
    /// Validates that the requested domain matches the configured runtime domain.
    /// This method ensures that clients can only access workflows within their authorized domain.
    /// </summary>
    /// <param name="requestDomain">The domain name being requested for access.</param>
    /// <exception cref="NotFoundDomainException">
    /// Thrown when the <paramref name="requestDomain"/> does not match the configured runtime domain.
    /// This indicates an unauthorized access attempt to a different domain.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="requestDomain"/> is null.</exception>
    void Check(string requestDomain);

    /// <summary>
    /// Checks if the specified domain matches the configured runtime domain.
    /// This is a non-throwing alternative to <see cref="Check"/> for scenarios where
    /// domain mismatch should be handled gracefully (e.g., event filtering).
    /// </summary>
    /// <param name="requestDomain">The domain name to compare against the runtime domain.</param>
    /// <returns>
    /// <c>true</c> if the <paramref name="requestDomain"/> matches the configured runtime domain (case-insensitive);
    /// otherwise, <c>false</c>.
    /// </returns>
    bool IsDomainMatch(string? requestDomain);

    /// <summary>
    /// Checks if the specified domain is the same as the current runtime domain.
    /// This is a simple synchronous check without any cache lookup.
    /// </summary>
    /// <param name="domain">The domain name to check.</param>
    /// <returns>
    /// <c>true</c> if the domain matches the current runtime domain (case-insensitive);
    /// otherwise, <c>false</c>.
    /// </returns>
    bool IsSameDomain(string? domain);

    /// <summary>
    /// Validates domain access asynchronously with cross-domain support.
    /// First checks if the domain matches the current runtime domain.
    /// If not, queries the distributed cache to verify if the domain is registered.
    /// </summary>
    /// <param name="requestDomain">The domain name being requested for access.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous validation operation.</returns>
    /// <exception cref="NotFoundDomainException">
    /// Thrown when the domain is not the current runtime domain and is not found in the cache.
    /// </exception>
    Task CheckAsync(string requestDomain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets registered domain information from the distributed cache.
    /// This method retrieves domain registry information that was stored by the domain-registration workflow.
    /// </summary>
    /// <param name="domain">The domain name to look up.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task containing the <see cref="DomainRegistryInfo"/> if found in cache;
    /// otherwise, <c>null</c>.
    /// </returns>
    Task<DomainRegistryInfo?> GetDomainInfoAsync(string domain, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class RuntimeInfoProvider : IRuntimeInfoProvider
{
    private readonly IDistributedCacheService _distributedCache;

    /// <summary>
    /// Cache key prefix for domain registry entries.
    /// </summary>
    private const string DomainCacheKeyPrefix = "domain:";

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public string Domain { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeInfoProvider"/> class.
    /// </summary>
    /// <param name="distributedCache">The distributed cache service for cross-domain lookups.</param>
    public RuntimeInfoProvider(IDistributedCacheService distributedCache)
    {
        _distributedCache = distributedCache;
        Version = Environment.GetEnvironmentVariable("APP_VERSION") ?? GetAssemblyVersion();
        Domain = Environment.GetEnvironmentVariable("APP_DOMAIN") ?? "unknown";

        if (Version == "unknown" || Domain == "unknown")
        {
            throw new AetherException("APP_VERSION and APP_DOMAIN environment variables must be set.");
        }
    }

    /// <inheritdoc />
    public void Check(string requestDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestDomain);
        if (!IsDomainMatch(requestDomain))
        {
            throw new NotFoundDomainException(requestDomain, Domain);
        }
    }

    /// <inheritdoc />
    public bool IsDomainMatch(string? requestDomain)
    {
        return !string.IsNullOrWhiteSpace(requestDomain) &&
               Domain.Equals(requestDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool IsSameDomain(string? domain)
    {
        return IsDomainMatch(domain);
    }

    /// <inheritdoc />
    public async Task CheckAsync(string requestDomain, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestDomain);

        // Fast path: same domain
        if (IsSameDomain(requestDomain))
        {
            return;
        }

        // Cross-domain: check if domain is registered in cache
        var domainInfo = await GetDomainInfoAsync(requestDomain, cancellationToken);
        if (domainInfo == null)
        {
            throw new NotFoundDomainException(requestDomain, Domain);
        }
    }

    /// <inheritdoc />
    public async Task<DomainRegistryInfo?> GetDomainInfoAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var cacheKey = $"{DomainCacheKeyPrefix}{domain}";
        return await _distributedCache.GetAsync<DomainRegistryInfo>(cacheKey, cancellationToken);
    }

    /// <summary>
    /// Retrieves the version information from the executing assembly's metadata.
    /// This method serves as a fallback when the APP_VERSION environment variable is not set.
    /// </summary>
    /// <returns>
    /// The assembly version string obtained from <see cref="AssemblyInformationalVersionAttribute"/>,
    /// assembly version, or "unknown" if no version information is available.
    /// </returns>
    /// <remarks>
    /// The method prioritizes version information in the following order:
    /// 1. AssemblyInformationalVersionAttribute.InformationalVersion
    /// 2. Assembly.GetName().Version
    /// 3. "unknown" as a last resort
    /// </remarks>
    private string GetAssemblyVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var versionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return assembly.GetName().Version?.ToString() ?? versionAttribute?.InformationalVersion ?? "unknown";
    }
}