using BBT.Aether.DistributedLock;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Schemas;

/// <summary>
/// Orchestrates schema migrations with distributed locking to ensure safe concurrent execution.
/// </summary>
public sealed class SchemaMigrationOrchestrator(
    IMultiSchemaMigrator<WorkflowDbContext> migrator,
    IDistributedLockService lockService,
    ILogger<SchemaMigrationOrchestrator> logger) : ISchemaMigrationOrchestrator
{
    private const int LockExpiryInSeconds = 120; // 2 minutes
    private const string LockKeyPrefix = "schema-migration";

    /// <inheritdoc />
    public async Task<bool> MigrateSchemaWithLockAsync(string schemaName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new ArgumentNullException(nameof(schemaName), "Schema name cannot be null or empty");

        var lockKey = $"{LockKeyPrefix}:{schemaName}";

        try
        {
            // Try to acquire distributed lock for this schema
            var lockOutcome = await lockService.ExecuteWithLockAsync(
                lockKey,
                async () =>
                {
                    await migrator.MigrateSchemaAsync(schemaName, cancellationToken);
                },
                LockExpiryInSeconds,
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

