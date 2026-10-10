using System.Reflection;
using BBT.Aether;
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
    /// Gets the domain the current operation serves.
    /// </summary>
    /// <value>
    /// Inside a <see cref="UseDomain"/> scope, that scope's domain; otherwise the primary hosted domain
    /// (the first entry of <see cref="HostedDomains"/>). With a single hosted domain this is always that
    /// domain, as before multi-domain hosting.
    /// </value>
    string Domain { get; }

    /// <summary>
    /// Gets every domain this process serves, primary first: <c>APP_DOMAIN</c> (the primary, which
    /// names this process's Dapr app-ids) followed by the comma-separated <c>APP_DOMAINS</c>.
    /// </summary>
    IReadOnlyList<string> HostedDomains { get; }

    /// <summary>
    /// Validates that the requested domain is hosted by this process.
    /// This method ensures that clients can only access workflows within a served domain.
    /// </summary>
    /// <param name="requestDomain">The domain name being requested for access.</param>
    /// <exception cref="NotFoundDomainException">
    /// Thrown when the <paramref name="requestDomain"/> is not one of <see cref="HostedDomains"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="requestDomain"/> is null.</exception>
    void Check(string requestDomain);

    /// <summary>
    /// Checks if the specified domain is hosted by this process.
    /// This is a non-throwing alternative to <see cref="Check"/> for scenarios where
    /// a foreign domain should be handled gracefully (e.g., event filtering, local-vs-remote routing).
    /// </summary>
    /// <param name="requestDomain">The domain name to look up.</param>
    /// <returns>
    /// <c>true</c> if the <paramref name="requestDomain"/> is one of <see cref="HostedDomains"/>
    /// (case-insensitive); otherwise, <c>false</c>.
    /// </returns>
    bool IsDomainMatch(string? requestDomain);

    /// <summary>
    /// Makes <paramref name="domain"/> the <see cref="Domain"/> of the current logical operation until
    /// the returned scope is disposed. Every entry point (HTTP request, job, inbox event, relayed
    /// command) opens one so that code reading <see cref="Domain"/> sees the domain being served.
    /// </summary>
    /// <param name="domain">A hosted domain.</param>
    /// <returns>A scope restoring the enclosing domain on dispose.</returns>
    /// <exception cref="NotFoundDomainException">
    /// Thrown when <paramref name="domain"/> is not one of <see cref="HostedDomains"/>.
    /// </exception>
    IDisposable UseDomain(string domain);
}

/// <inheritdoc />
public class RuntimeInfoProvider : IRuntimeInfoProvider
{
    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public string Domain => Resolve(DomainScope.Current) ?? _primaryDomain;

    /// <inheritdoc />
    public IReadOnlyList<string> HostedDomains { get; }

    private readonly string _primaryDomain;

    /// <inheritdoc />
    public void Check(string requestDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestDomain);
        if (!IsDomainMatch(requestDomain))
        {
            throw new NotFoundDomainException(requestDomain, string.Join(",", HostedDomains));
        }
    }

    /// <inheritdoc />
    public bool IsDomainMatch(string? requestDomain)
    {
        return Resolve(requestDomain) is not null;
    }

    /// <inheritdoc />
    public IDisposable UseDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var hosted = Resolve(domain) ?? throw new NotFoundDomainException(domain, string.Join(",", HostedDomains));
        return DomainScope.Begin(hosted);
    }

    public RuntimeInfoProvider()
        : this(
            Environment.GetEnvironmentVariable("APP_VERSION"),
            Environment.GetEnvironmentVariable("APP_DOMAINS"),
            Environment.GetEnvironmentVariable("APP_DOMAIN"))
    {
    }

    /// <summary>
    /// Builds the provider from explicit values (the parameterless constructor reads them from the
    /// environment). <paramref name="domain"/> is the primary and required; <paramref name="domains"/>
    /// optionally adds co-hosted domains.
    /// </summary>
    public RuntimeInfoProvider(string? version, string? domains, string? domain)
    {
        Version = version ?? GetAssemblyVersion();
        HostedDomains = ParseDomains(domains, domain);

        if (Version == "unknown" || HostedDomains.Count == 0)
        {
            throw new AetherException("APP_VERSION and APP_DOMAIN environment variables must be set.");
        }

        _primaryDomain = HostedDomains[0];
    }

    /// <summary>
    /// Returns the hosted spelling of <paramref name="requestDomain"/>, or <c>null</c> when it is not hosted.
    /// </summary>
    private string? Resolve(string? requestDomain)
    {
        if (string.IsNullOrWhiteSpace(requestDomain))
        {
            return null;
        }

        foreach (var hosted in HostedDomains)
        {
            if (hosted.Equals(requestDomain, StringComparison.OrdinalIgnoreCase))
            {
                return hosted;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>APP_DOMAIN</c> is the primary domain and always comes first: it names this process's Dapr
    /// app-ids (<c>vnext-{APP_DOMAIN}-*</c>, read straight from configuration by the invokers and the
    /// inbox forwarder), so in multi-domain hosting the pool is addressed by its primary domain.
    /// <c>APP_DOMAINS</c> adds the co-hosted domains.
    /// </summary>
    private static IReadOnlyList<string> ParseDomains(string? domains, string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return [];
        }

        var source = string.IsNullOrWhiteSpace(domains) ? domain : $"{domain},{domains}";

        var result = new List<string>();
        foreach (var part in source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!result.Exists(existing => existing.Equals(part, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(part);
            }
        }

        return result;
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