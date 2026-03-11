using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.DbMigrator;

/// <summary>
/// Runs database schema migrations in two phases: system schemas first, then domain schemas discovered from sys_flows.
/// Used by the DbMigrator job at deploy/upgrade time.
/// </summary>
public sealed class SchemaMigrationRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<SchemaMigrationRunner> logger)
{
    /// <summary>
    /// Gets whether the last run completed successfully.
    /// </summary>
    public bool Success { get; private set; }

    /// <summary>
    /// Executes the two-phase schema migration process.
    /// Returns only after all migrations (including all parallel domain schema migrations) have fully completed.
    /// Process must not exit until this method returns to avoid cutting migrations short.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Success = false;
        logger.LogInformation("DbMigrator schema migration started");

        try
        {
            await MigrateSystemSchemasAsync(cancellationToken);
            await MigrateDomainSchemasAsync(cancellationToken);

            // Only after both phases have fully completed (all parallel tasks finished) we set success and log.
            Success = true;
            logger.LogInformation(
                "All migrations completed successfully. Safe to exit process.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Critical error during schema migration");
            throw;
        }
    }

    private async Task MigrateSystemSchemasAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runtimeOptions = scope.ServiceProvider.GetRequiredService<IOptions<RuntimeOptions>>();

        if (!runtimeOptions.Value.EnableSchemaMigration)
        {
            logger.LogInformation("Schema migration is disabled. Skipping system schema migrations");
            return;
        }

        var systemSchemas = runtimeOptions.Value.Schemas.Values.ToList();
        if (systemSchemas.Count == 0)
        {
            logger.LogWarning("No system schemas found in RuntimeOptions");
            return;
        }

        logger.LogInformation("Starting migration of {Count} system schemas", systemSchemas.Count);
        foreach (var schemaInfo in systemSchemas)
        {
            await MigrateSingleSchemaAsync(schemaInfo.Schema, cancellationToken).ConfigureAwait(false);
        }
        logger.LogInformation("Completed migration of system schemas");
    }

    private async Task MigrateDomainSchemasAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runtimeOptions = scope.ServiceProvider.GetRequiredService<IOptions<RuntimeOptions>>();

        if (!runtimeOptions.Value.EnableSchemaMigration)
        {
            logger.LogInformation("Schema migration is disabled. Skipping domain schema migrations");
            return;
        }

        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var dbContext = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        try
        {
            using (currentSchema.Use(RuntimeSysSchemaInfo.Flows))
            {
                List<string> domainSchemas;
                try
                {
                    domainSchemas = await dbContext.Instances
                        .Where(i => i.Key != null)
                        .Select(i => i.Key!)
                        .Distinct()
                        .ToListAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to query domain schemas from sys_flows. Schema may not exist yet");
                    return;
                }

                if (domainSchemas.Count == 0)
                {
                    logger.LogInformation("No domain schemas found in sys_flows");
                    return;
                }

                var systemSchemaNames = runtimeOptions.Value.Schemas.Values
                    .Select(s => s.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var schemasToMigrate = domainSchemas
                    .Where(key => !systemSchemaNames.Contains(key))
                    .ToList();

                if (schemasToMigrate.Count == 0)
                {
                    logger.LogInformation("All discovered schemas are system schemas. No domain schemas to migrate");
                    return;
                }

                logger.LogInformation(
                    "Found {TotalCount} schemas in sys_flows, {MigrateCount} domain schemas to migrate",
                    domainSchemas.Count,
                    schemasToMigrate.Count);

                await MigrateSchemasInParallelAsync(schemasToMigrate, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during domain schema migration process");
            throw;
        }
    }

    /// <summary>
    /// Migrates multiple schemas in parallel. Returns only when every schema migration has finished (success or logged failure); process must not exit before this returns.
    /// </summary>
    private async Task MigrateSchemasInParallelAsync(List<string> schemas, CancellationToken cancellationToken)
    {
        var maxConcurrency = Environment.ProcessorCount;
        if (maxConcurrency <= 0)
        {
            maxConcurrency = 4;
        }
        
        maxConcurrency = Math.Min(maxConcurrency, schemas.Count);
        
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = schemas.Select(schema => MigrateWithLimitAsync(schema, cancellationToken)).ToArray();
        
        await Task.WhenAll(tasks);
        
        logger.LogInformation(
            "Completed parallel migration of {Count} domain schemas with max concurrency {MaxConcurrency}; all tasks finished.",
            schemas.Count,
            maxConcurrency);
        
        async Task MigrateWithLimitAsync(string schemaName, CancellationToken ct)
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await MigrateSingleSchemaAsync(schemaName, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }
        
        logger.LogInformation(
            "Completed parallel migration of {Count} domain schemas; all tasks finished.",
            schemas.Count);
    }

    private async Task MigrateSingleSchemaAsync(string schemaName, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        using (currentSchema.Use(schemaName))
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<ISchemaMigrationOrchestrator>();
            try
            {
                await orchestrator.MigrateSchemaWithLockAsync(schemaName, cancellationToken);
                logger.LogInformation("Migration completed for schema {Schema}", schemaName);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Migration failed for schema {Schema}. Continuing with remaining schemas",
                    schemaName);
                // Don't rethrow - allow other schemas to continue
            }
        }
    }
}
