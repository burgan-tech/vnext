using System.Data.Common;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow;

internal sealed class SqliteAetherProvider(SqliteConnection connection) : IAetherDatabaseProvider
{
    public DbConnection CreateConnection(string connectionString) => connection;

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection, string schema, SchemaScopeState state)
        => builder.UseSqlite(sharedConnection);

    public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseSqlite(connection);
}
