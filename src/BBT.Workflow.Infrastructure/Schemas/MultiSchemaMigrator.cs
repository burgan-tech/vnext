using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    ILogger<MultiSchemaMigrator<TContext>> logger,
    ICurrentSchema currentSchema,
    IOptions<SchemaMigrationOptions> options
) : IMultiSchemaMigrator<TContext>, ITargetedSchemaMigrator
    where TContext : DbContext
{
    /// <inheritdoc />
    public async Task MigrateSchemaAsync(string schema, CancellationToken cancellationToken = default)
    {
        var schemaName = FormatSchemaName(schema);

        logger.LogInformation("Starting migration for schema: {Schema}", schemaName);

        using (currentSchema.Change(schema))
        {
            await using var ctx = CreateIsolatedContext(schemaName);

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
    }

    /// <inheritdoc />
    public async Task<SchemaMigrationStatus> GetStatusAsync(
        string schema, CancellationToken cancellationToken = default)
    {
        var schemaName = FormatSchemaName(schema);
        using (currentSchema.Change(schema))
        {
            await using var ctx = CreateIsolatedContext(schemaName);
            var (applied, pending, unknown) = await MigrationChainInspector.GetChainStateAsync(ctx, cancellationToken);
            return new SchemaMigrationStatus(
                schema, applied.Count > 0 ? applied[^1] : null, applied.Count, pending, unknown);
        }
    }

    /// <inheritdoc />
    public async Task<SchemaMigrationPlan> PlanMigrationToTargetAsync(
        string schema, string targetMigration, CancellationToken cancellationToken = default)
    {
        var schemaName = FormatSchemaName(schema);
        using (currentSchema.Change(schema))
        {
            await using var ctx = CreateIsolatedContext(schemaName);
            return await MigrationChainInspector.PlanAsync(ctx, schema, targetMigration, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task MigrateSchemaToTargetAsync(
        string schema, string targetMigration, CancellationToken cancellationToken = default)
    {
        var schemaName = FormatSchemaName(schema);
        using (currentSchema.Change(schema))
        {
            await using var ctx = CreateIsolatedContext(schemaName);
            var targetId = MigrationChainInspector.ResolveTargetMigrationId(ctx, targetMigration);

            logger.LogInformation(
                "Starting targeted migration for schema {Schema} to {TargetMigration}", schemaName, targetId);

            // Same converge call the EF tools use: reverts (Down, newest first) when the schema is
            // above the target, applies (Up) when it is below, one migration per transaction.
            await ctx.GetService<IMigrator>().MigrateAsync(targetId, cancellationToken);

            logger.LogInformation(
                "Targeted migration completed for schema {Schema} at {TargetMigration}", schemaName, targetId);
        }
    }

    /// <inheritdoc />
    public async Task<string> GenerateScriptToTargetAsync(
        string schema, string targetMigration, CancellationToken cancellationToken = default)
    {
        var schemaName = FormatSchemaName(schema);
        using (currentSchema.Change(schema))
        {
            await using var ctx = CreateIsolatedContext(schemaName);
            return await MigrationChainInspector.GenerateScriptAsync(ctx, targetMigration, cancellationToken);
        }
    }

    private string FormatSchemaName(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            throw new ArgumentException("Schema name cannot be null or empty.", nameof(schema));

        return schemaNameFormatter.Format(schema);
    }

    /// <summary>
    /// Builds the isolated migration context every operation here runs against: per-schema history
    /// table, long command timeout, the schema-rewriting SQL generator, and no reuse of the
    /// DI-registered runtime context. The caller owns the enclosing <c>currentSchema.Change(...)</c>
    /// scope (the context captures <see cref="ICurrentSchema"/> by reference).
    /// </summary>
    private WorkflowDbContext CreateIsolatedContext(string schemaName)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        var optionsBuilder = new DbContextOptionsBuilder<WorkflowDbContext>();
        optionsBuilder
            .UseNpgsql(connectionString, npgsql =>
            {
                // Schema-qualified history table — preserves existing migration records.
                npgsql.MigrationsHistoryTable("__Workflow_Migrations", schemaName);
                // Data migrations over large tables outlive Npgsql's 30 s default (SchemaMigration section).
                npgsql.CommandTimeout(options.Value.CommandTimeoutSeconds);
            })
            // SchemaAwareModelCacheKeyFactory produces a different compiled model per schema,
            // so EF Core's snapshot-diff check always sees a "mismatch". This is by design —
            // suppress the warning for the isolated migration context.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        optionsBuilder.ReplaceService<IMigrationsSqlGenerator, MultiSchemaNpgsqlMigrationsSqlGenerator>();
        // Partition the compiled-model cache by schema here too, not only in the runtime contexts:
        // EF shares one internal service provider (and with the default factory, ONE type-keyed
        // model) across option sets that differ only in runtime values like the history table —
        // so without this, whichever schema builds the model first pins its table mappings for
        // every other schema in the same process.
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>();

        return new WorkflowDbContext(optionsBuilder.Options, currentSchema);
    }

    private static async Task EnsureSchemaExistsAsync(
        DbContext ctx, string schema, CancellationToken cancellationToken)
    {
        await ctx.Database.ExecuteSqlRawAsync(
            $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"", cancellationToken);
    }
}
