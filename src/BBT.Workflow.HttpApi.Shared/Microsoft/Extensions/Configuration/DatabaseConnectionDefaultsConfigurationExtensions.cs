using Npgsql;

namespace Microsoft.Extensions.Configuration;

/// <summary>
/// Normalises the PostgreSQL connection strings of a host before any service reads them.
/// <para>
/// Every host (orchestration, inbox, outbox, migrator) opens its own Npgsql pool per connection
/// string, and Npgsql's default <c>Maximum Pool Size</c> is 100 per pool. Four hosts against one
/// database can therefore ask for 400 connections from a server configured for 100: under a burst
/// the server answers <c>53300: sorry, too many clients already</c>, and a pipeline step that loses
/// that race faults its instance (measured on the contract domain at concurrency 20). The pool also
/// stayed full after the burst, and with an empty <c>application_name</c> nobody could tell which
/// host held the connections.
/// </para>
/// <para>
/// This step only fills in what the connection string does not already say:
/// <list type="bullet">
/// <item><c>Application Name</c> — always, from <c>OTEL_SERVICE_NAME</c>, <c>DAPR_APP_ID</c> or the
/// given fallback, so <c>pg_stat_activity</c> shows the owning host.</item>
/// <item><c>Maximum Pool Size</c> — only when <c>Database:MaxPoolSize</c> is configured. The sum of
/// (pods × hosts × pool size) must stay below the server's <c>max_connections</c>; with a pool limit a
/// burst waits for a free connection (<c>Timeout</c>) instead of being refused by the server.</item>
/// <item><c>Connection Idle Lifetime</c> — only when <c>Database:ConnectionIdleLifetimeSeconds</c> is
/// configured, so a pool shrinks back after a burst.</item>
/// </list>
/// An explicit value in the connection string always wins; with no <c>Database</c> section the
/// only change is the application name.
/// </para>
/// </summary>
public static class DatabaseConnectionDefaultsConfigurationExtensions
{
    /// <summary>Configuration section of the pool defaults.</summary>
    public const string Section = "Database";

    /// <summary>
    /// Applies the defaults to <c>ConnectionStrings:Default</c> and every
    /// <c>ConnectionStrings:Domains:*</c> entry and layers the result over the existing sources.
    /// Call after every source that may supply a connection string (secret store included).
    /// </summary>
    /// <param name="configuration">The host's configuration manager.</param>
    /// <param name="fallbackApplicationName">Used when neither OTEL_SERVICE_NAME nor DAPR_APP_ID is set.</param>
    public static IConfigurationManager ApplyDatabaseConnectionDefaults(
        this IConfigurationManager configuration,
        string fallbackApplicationName)
    {
        var applicationName = FirstNonEmpty(
            configuration["OTEL_SERVICE_NAME"],
            configuration["DAPR_APP_ID"],
            fallbackApplicationName);
        var maxPoolSize = configuration.GetValue<int?>($"{Section}:MaxPoolSize");
        var idleLifetime = configuration.GetValue<int?>($"{Section}:ConnectionIdleLifetimeSeconds");

        var overrides = new Dictionary<string, string?>();
        Normalise("ConnectionStrings:Default");
        foreach (var entry in configuration.GetSection("ConnectionStrings:Domains").GetChildren())
        {
            Normalise(entry.Path);
        }

        if (overrides.Count > 0)
        {
            configuration.AddInMemoryCollection(overrides);
        }

        return configuration;

        void Normalise(string key)
        {
            var raw = configuration[key];
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var normalised = Apply(raw, applicationName, maxPoolSize, idleLifetime);
            if (!string.Equals(normalised, raw, StringComparison.Ordinal))
            {
                overrides[key] = normalised;
            }
        }
    }

    /// <summary>
    /// Applies the defaults to one connection string; values already present are kept.
    /// </summary>
    public static string Apply(string connectionString, string? applicationName, int? maxPoolSize, int? idleLifetimeSeconds)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Not a connection string this step understands: leave it to the provider to reject.
            return connectionString;
        }

        if (string.IsNullOrWhiteSpace(builder.ApplicationName) && !string.IsNullOrWhiteSpace(applicationName))
        {
            builder.ApplicationName = applicationName;
        }

        if (maxPoolSize is > 0 && !HasKey(connectionString, "Maximum Pool Size", "Max Pool Size", "MaxPoolSize"))
        {
            builder.MaxPoolSize = maxPoolSize.Value;
            if (builder.MinPoolSize > builder.MaxPoolSize)
            {
                builder.MinPoolSize = builder.MaxPoolSize;
            }
        }

        if (idleLifetimeSeconds is > 0 && !HasKey(connectionString, "Connection Idle Lifetime", "ConnectionIdleLifetime"))
        {
            builder.ConnectionIdleLifetime = idleLifetimeSeconds.Value;
        }

        return builder.ConnectionString;
    }

    private static bool HasKey(string connectionString, params string[] keys) =>
        connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0].Trim())
            .Any(name => keys.Any(k => string.Equals(name, k, StringComparison.OrdinalIgnoreCase)));

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
