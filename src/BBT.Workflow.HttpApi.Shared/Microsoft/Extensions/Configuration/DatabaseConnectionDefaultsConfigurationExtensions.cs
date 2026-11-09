using Npgsql;

namespace Microsoft.Extensions.Configuration;

/// <summary>
/// Normalises the PostgreSQL connection strings of a host before any service reads them.
/// <para>
/// It only adds <c>Application Name</c>, taken from <c>OTEL_SERVICE_NAME</c>, <c>DAPR_APP_ID</c> or the
/// given fallback, so <c>pg_stat_activity</c> (and PgBouncer's <c>SHOW CLIENTS</c>) shows which host
/// owns a connection; with an empty <c>application_name</c> nobody could tell. A name already present in
/// the connection string wins, and nothing else in the string is changed.
/// </para>
/// <para>
/// Pool sizing is deliberately left to the connection string and the deployment: in production the
/// hosts connect through PgBouncer, which owns the server-side connection limit, so a client-side
/// default here would only second-guess it.
/// </para>
/// </summary>
public static class DatabaseConnectionDefaultsConfigurationExtensions
{
    /// <summary>
    /// Applies the application name to <c>ConnectionStrings:Default</c> and every
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

            var normalised = Apply(raw, applicationName);
            if (!string.Equals(normalised, raw, StringComparison.Ordinal))
            {
                overrides[key] = normalised;
            }
        }
    }

    /// <summary>
    /// Adds the application name to one connection string unless it already states one.
    /// </summary>
    public static string Apply(string connectionString, string? applicationName)
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

        if (!string.IsNullOrWhiteSpace(builder.ApplicationName) || string.IsNullOrWhiteSpace(applicationName))
        {
            return connectionString;
        }

        builder.ApplicationName = applicationName;
        return builder.ConnectionString;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
