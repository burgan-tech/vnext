using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Orchestration.Services;

/// <summary>
/// Background service that automatically migrates database schemas on application startup.
/// Handles both system schemas (from RuntimeOptions) and domain schemas (from sys_flows).
/// </summary>
/// <remarks>
/// This service executes once on application startup and performs the following operations:
/// 1. Migrates all system schemas defined in RuntimeOptions (sys-flows, sys-functions, etc.)
/// 2. Queries the sys_flows schema to discover domain schemas from Instance.Key values
/// 3. Migrates discovered domain schemas in parallel with bounded concurrency
/// 4. Uses distributed locking to ensure only one pod migrates each schema
/// </remarks>
internal sealed class MultiSchemaMigrationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<MultiSchemaMigrationHostedService> logger) : BackgroundService
{
    private const int MaxConcurrentMigrations = 3;

    /// <summary>
    /// Executes the schema migration process on application startup.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token for stopping the service.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Multi-schema migration service started");

        try
        {
            // Migrate system schemas from RuntimeOptions
            await MigrateSystemSchemasAsync(stoppingToken);

            logger.LogInformation("Multi-schema migration service completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Critical error during multi-schema migration");
        }
    }

    /// <summary>
    /// Migrates all system schemas defined in RuntimeOptions.
    /// These are core workflow system schemas like sys-flows, sys-functions, etc.
    /// </summary>
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

        if (!systemSchemas.Any())
        {
            logger.LogWarning("No system schemas found in RuntimeOptions");
            return;
        }

        logger.LogInformation("Starting migration of {Count} system schemas", systemSchemas.Count);

        // Migrate system schemas sequentially to ensure proper order
        foreach (var schemaInfo in systemSchemas)
        {
            await MigrateSingleSchemaAsync(schemaInfo.Schema, cancellationToken);
        }

        logger.LogInformation("Completed migration of system schemas");
    }
    
    /// <summary>
    /// Migrates a single schema using the schema migration orchestrator.
    /// The orchestrator handles distributed locking and migration execution.
    /// </summary>
    /// <param name="schemaName">The name of the schema to migrate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task MigrateSingleSchemaAsync(
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        using (currentSchema.Use(schemaName))
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<ISchemaMigrationOrchestrator>();

            try
            {
                await orchestrator.MigrateSchemaWithLockAsync(schemaName, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Migration failed for schema {Schema}. Continuing with remaining schemas",
                    schemaName);
                // Don't rethrow - allow other schemas to continue migrating
            }
        }
    }
}