using System.Data.Common;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.Runtime;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BBT.Workflow.Data;

/// <summary>
/// Points Aether's unit-of-work DbContexts at the current domain's database under multi-domain hosting
/// with dedicated databases (<c>ConnectionStrings:Domains:&lt;domain&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// Decorates the configurator <c>AddAetherNpgsql</c> registers, which captured
/// <c>ConnectionStrings:Default</c> at startup. For a domain on the default database every call is
/// passed through untouched, so a single-domain process and a shared-database pool behave exactly as
/// before. For a dedicated domain the connection (transactional roots) or the owned connection string
/// (non-transactional roots) is swapped; all other options — interceptors, model cache key, migrations
/// history table — come from the inner configurator.
/// </para>
/// <para>
/// The domain is read on every call from <see cref="IDomainDatabaseResolver"/>, i.e. from the ambient
/// <see cref="DomainScope"/>. One unit-of-work root holds one connection, so a root must not span two
/// databases; cross-database calls between co-hosted domains open a new root
/// (<c>Gateway.CoHostedDatabaseBoundary</c>).
/// </para>
/// </remarks>
public sealed class DomainAwareDbContextConfigurator<TDbContext>(
    IAetherDbContextConfigurator<TDbContext> inner,
    IDomainDatabaseResolver resolver) : IAetherDbContextConfigurator<TDbContext>
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public DbConnection CreateConnection() =>
        DedicatedConnectionString() is { } connectionString
            ? new NpgsqlConnection(connectionString)
            : inner.CreateConnection();

    /// <inheritdoc />
    public DbContextOptions<TDbContext> BuildOptions(DbConnection sharedConnection, string schema, SchemaScopeState state) =>
        inner.BuildOptions(sharedConnection, schema, state);

    /// <inheritdoc />
    public DbContextOptions<TDbContext> BuildOwnedOptions(string schema)
    {
        var options = inner.BuildOwnedOptions(schema);
        if (DedicatedConnectionString() is not { } connectionString)
        {
            return options;
        }

        // UseNpgsql on an existing builder only replaces the connection string of the Npgsql extension;
        // the migrations history table and every other provider setting are kept.
        return new DbContextOptionsBuilder<TDbContext>(options).UseNpgsql(connectionString).Options;
    }

    private string? DedicatedConnectionString() =>
        resolver.HasDedicatedDatabase() ? resolver.ConnectionStringFor() : null;
}
