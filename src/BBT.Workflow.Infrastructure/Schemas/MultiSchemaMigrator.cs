using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Schemas;

/// <summary>
/// Migrates a PostgreSQL schema to the latest EF Core migration in an isolated context.
///
/// Each call builds a fresh <see cref="WorkflowDbContext"/> with its own
/// <see cref="DbContextOptions{TContext}"/> so that:
/// <list type="bullet">
///   <item>The migration history table is fully qualified: <c>schema.__Workflow_Migrations</c>.</item>
///   <item>Entity table mappings are schema-qualified via <c>StaticCurrentSchema</c>.</item>
///   <item>No <c>SET search_path</c> is ever issued — safe under PgBouncer.</item>
///   <item>The runtime <see cref="WorkflowDbContext"/> (registered in DI) is never reused,
///         keeping migration and runtime lifecycles fully independent.</item>
/// </list>
/// </summary>
public sealed class MultiSchemaMigrator<TContext>(
    IConfiguration configuration,
    ISchemaNameFormatter schemaNameFormatter,
    ILogger<MultiSchemaMigrator<TContext>> logger
) : IMultiSchemaMigrator<TContext>
    where TContext : DbContext
{
    /// <inheritdoc />
    public async Task MigrateSchemaAsync(string schema, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schema))
            throw new ArgumentException("Schema name cannot be null or empty.", nameof(schema));

        var schemaName = schemaNameFormatter.Format(schema);

        logger.LogInformation("Starting migration for schema: {Schema}", schemaName);

        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        var optionsBuilder = new DbContextOptionsBuilder<WorkflowDbContext>();
        optionsBuilder
            .UseNpgsql(connectionString, npgsql =>
            {
                // Schema-qualified history table — preserves existing migration records.
                npgsql.MigrationsHistoryTable("__Workflow_Migrations", schemaName);
            })
            // SchemaAwareModelCacheKeyFactory produces a different compiled model per schema,
            // so EF Core's snapshot-diff check always sees a "mismatch". This is by design —
            // suppress the warning for the isolated migration context.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        optionsBuilder.ReplaceService<IMigrationsSqlGenerator, MultiSchemaNpgsqlMigrationsSqlGenerator>();

        // WorkflowDbContext requires ICurrentSchema. Use a static wrapper so the migration
        // context maps entity tables to the target schema without touching the DI-scoped instance.
        var staticSchema = new StaticCurrentSchema(schemaName);
        await using var ctx = new WorkflowDbContext(optionsBuilder.Options, staticSchema);

        await EnsureSchemaExistsAsync(ctx, schemaName, cancellationToken);

        // MigrateAsync is fully idempotent:
        //   - creates __Workflow_Migrations history table if it does not exist yet
        //   - applies only the migrations not yet recorded in the history table
        //   - no-ops when all migrations are already applied
        // GetPendingMigrationsAsync is intentionally avoided because it issues a SELECT
        // against the history table before MigrateAsync has had a chance to create it,
        // causing a "relation does not exist" error on a brand-new schema.
        await ctx.Database.MigrateAsync(cancellationToken);

        logger.LogInformation("Migration completed for schema: {Schema}", schemaName);
    }

    private static async Task EnsureSchemaExistsAsync(
        DbContext ctx, string schema, CancellationToken cancellationToken)
    {
        await ctx.Database.ExecuteSqlRawAsync(
            $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"", cancellationToken);
    }
}

/// <summary>
/// Minimal <see cref="ICurrentSchema"/> implementation that always returns a fixed schema name.
/// Used exclusively during migration to inject the target schema into <see cref="WorkflowDbContext"/>
/// without involving the DI-scoped <see cref="ICurrentSchema"/> resolver.
/// </summary>
internal sealed class StaticCurrentSchema : ICurrentSchema
{
    private string _name;

    public StaticCurrentSchema(string name) => _name = name;

    /// <inheritdoc />
    public string? Name => _name;

    /// <inheritdoc />
    public bool IsResolved => true;

    /// <inheritdoc />
    public void Set(string schema) => _name = schema;
}
