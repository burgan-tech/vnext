using BBT.Aether.DistributedLock;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Schemas;

/// <summary>
/// Orchestrates schema migrations with distributed locking to ensure safe concurrent execution.
/// </summary>
public sealed class SchemaMigrationOrchestrator(
    IMultiSchemaMigrator<WorkflowDbContext> migrator,
    IDistributedLockService lockService,
    IOptions<SchemaMigrationOptions> options,
    ILogger<SchemaMigrationOrchestrator> logger,
    ISchemaNameFormatter? schemaNameFormatter = null) : ISchemaMigrationOrchestrator
{
    private const string LockKeyPrefix = "schema-migration";

    /// <inheritdoc />
    public async Task<bool> MigrateSchemaWithLockAsync(string schemaName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new ArgumentNullException(nameof(schemaName), "Schema name cannot be null or empty");

        // Keyed on the physical schema: with multi-domain hosting two co-hosted domains' same flow key
        // are different schemas, and a raw-name key would make the second migration skip.
        var lockKey = $"{LockKeyPrefix}:{schemaNameFormatter?.Format(schemaName) ?? schemaName}";

        try
        {
            // Try to acquire distributed lock for this schema
            var lockOutcome = await lockService.ExecuteWithLockAsync(
                lockKey,
                async () =>
                {
                    await migrator.MigrateSchemaAsync(schemaName, cancellationToken);
                },
                options.Value.LockExpirySeconds,
                cancellationToken);

            if (!lockOutcome)
            {
                logger.LogInformation(
                    "Schema {Schema} is already being migrated by another instance",
                    schemaName);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Migration failed for schema {Schema}",
                schemaName);
            throw new InvalidOperationException($"Failed to migrate schema '{schemaName}'", ex);
        }
    }
}

