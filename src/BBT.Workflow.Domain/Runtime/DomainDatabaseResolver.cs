using BBT.Aether;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Runtime;

/// <summary>
/// Connection strings for multi-domain hosting: the primary's (<c>ConnectionStrings:Default</c>) and
/// the optional dedicated database of each co-hosted domain (<c>ConnectionStrings:Domains:&lt;domain&gt;</c>).
/// </summary>
public sealed class DomainDatabaseOptions
{
    /// <summary>The primary domain's connection string; also every co-hosted domain without its own.</summary>
    public string? DefaultConnectionString { get; set; }

    /// <summary>Co-hosted domain → its own connection string. Empty: every domain shares the default.</summary>
    public Dictionary<string, string> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Which database a domain lives in, under multi-domain hosting.
/// </summary>
/// <remarks>
/// A co-hosted domain either shares the primary's database (its schemas are <c>&lt;domain&gt;_</c>-prefixed,
/// see <see cref="DomainSchemaNameFormatter"/>) or has a <b>dedicated</b> one, which keeps exactly the
/// single-domain layout — so an existing domain database joins a pool unchanged. With no
/// <c>ConnectionStrings:Domains</c> entry every answer is the default database: today's behaviour.
/// </remarks>
public interface IDomainDatabaseResolver
{
    /// <summary>The connection string of <paramref name="domain"/> (<c>null</c>: the current domain).</summary>
    string? ConnectionStringFor(string? domain = null);

    /// <summary>Whether <paramref name="domain"/> (<c>null</c>: the current domain) has its own database.</summary>
    bool HasDedicatedDatabase(string? domain = null);

    /// <summary>
    /// Whether two domains live in the same database. Code that crosses domains inside one unit of work
    /// must open a new unit-of-work root when this is false — one root holds one connection.
    /// </summary>
    bool SameDatabase(string? left, string? right);

    /// <summary>
    /// One hosted domain per distinct database, primary first. Per-database work (outbox, inbox, job
    /// arming, messaging migrations) runs once for each, under that domain's <see cref="DomainScope"/>.
    /// </summary>
    IReadOnlyList<string> DatabaseRepresentatives { get; }
}

/// <inheritdoc />
public sealed class DomainDatabaseResolver : IDomainDatabaseResolver
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly DomainDatabaseOptions _options;

    /// <summary>Validates the configuration: a dedicated database for a domain that is not hosted fails startup.</summary>
    public DomainDatabaseResolver(IRuntimeInfoProvider runtimeInfoProvider, IOptions<DomainDatabaseOptions> options)
    {
        _runtimeInfoProvider = runtimeInfoProvider;
        _options = options.Value;

        var primary = runtimeInfoProvider.HostedDomains is { Count: > 0 } hosted ? hosted[0] : null;
        foreach (var domain in _options.Domains.Keys)
        {
            if (!runtimeInfoProvider.IsDomainMatch(domain))
            {
                throw new AetherException(
                    $"ConnectionStrings:Domains:{domain} is configured, but '{domain}' is not hosted (APP_DOMAIN/APP_DOMAINS).");
            }

            if (string.Equals(domain, primary, StringComparison.OrdinalIgnoreCase))
            {
                throw new AetherException(
                    $"ConnectionStrings:Domains:{domain} names the primary domain; the primary always uses ConnectionStrings:Default.");
            }
        }

        DatabaseRepresentatives = BuildRepresentatives(primary);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> DatabaseRepresentatives { get; }

    /// <inheritdoc />
    public string? ConnectionStringFor(string? domain = null) =>
        DedicatedConnectionString(domain ?? _runtimeInfoProvider.Domain) ?? _options.DefaultConnectionString;

    /// <inheritdoc />
    public bool HasDedicatedDatabase(string? domain = null) =>
        DedicatedConnectionString(domain ?? _runtimeInfoProvider.Domain) is not null;

    /// <inheritdoc />
    public bool SameDatabase(string? left, string? right) =>
        string.Equals(ConnectionStringFor(left ?? _runtimeInfoProvider.Domain),
            ConnectionStringFor(right ?? _runtimeInfoProvider.Domain), StringComparison.Ordinal);

    private string? DedicatedConnectionString(string? domain) =>
        domain is not null
        && _options.Domains.TryGetValue(domain, out var connectionString)
        && !string.IsNullOrWhiteSpace(connectionString)
        && _runtimeInfoProvider.IsDomainMatch(domain)
            ? connectionString
            : null;

    private IReadOnlyList<string> BuildRepresentatives(string? primary)
    {
        var result = new List<string>();
        if (primary is not null)
        {
            result.Add(primary);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (_options.DefaultConnectionString is not null)
        {
            seen.Add(_options.DefaultConnectionString);
        }

        foreach (var domain in _runtimeInfoProvider.HostedDomains)
        {
            if (DedicatedConnectionString(domain) is { } connectionString && seen.Add(connectionString))
            {
                result.Add(domain);
            }
        }

        return result;
    }
}
