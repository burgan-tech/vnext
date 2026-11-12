using System.Data.Common;
using System.Text.Json;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BBT.Workflow;

internal sealed class SqliteAetherProvider(SqliteConnection connection) : IAetherDatabaseProvider
{
    public DbConnection CreateConnection(string connectionString) => connection;

    public void ApplyShared(DbContextOptionsBuilder builder, DbConnection sharedConnection, string schema, SchemaScopeState state)
        => UseSqlite(builder, sharedConnection);

    public void ApplyConnectionString(DbContextOptionsBuilder builder, string connectionString)
        => UseSqlite(builder, connection);

    private static void UseSqlite(DbContextOptionsBuilder builder, DbConnection sharedConnection)
        => builder
            .UseSqlite(sharedConnection)
            .ReplaceService<IModelCustomizer, SqliteJsonElementModelCustomizer>();
}

/// <summary>
/// Test-only model fix-up for the in-memory SQLite stand-in. Aether maps
/// <see cref="BackgroundJobInfo.Payload"/> (a <see cref="JsonElement"/>) to a <c>jsonb</c> column,
/// which Npgsql maps natively but SQLite cannot ("does not support mapping 'JsonElement' properties
/// to 'jsonb' columns"). After the production model is built, the column is stored as raw JSON text.
/// </summary>
internal sealed class SqliteJsonElementModelCustomizer(ModelCustomizerDependencies dependencies)
    : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        if (modelBuilder.Model.FindEntityType(typeof(BackgroundJobInfo)) is null)
        {
            return;
        }

        modelBuilder.Entity<BackgroundJobInfo>()
            .Property(e => e.Payload)
            .HasColumnType("TEXT")
            .HasConversion(
                v => v.GetRawText(),
                v => JsonDocument.Parse(v, new JsonDocumentOptions()).RootElement.Clone(),
                new ValueComparer<JsonElement>(
                    (a, b) => a.GetRawText() == b.GetRawText(),
                    v => v.GetRawText().GetHashCode(),
                    v => v.Clone()));
    }
}
