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
    ITargetedSchemaMigrator targetedMigrator,
    IDistributedLockService lockService,
    IOptions<SchemaMigrationOptions> options,
    ILogger<SchemaMigrationOrchestrator> logger) : ISchemaMigrationOrchestrator
{
    private const string LockKeyPrefix = "schema-migration";

    /// <inheritdoc />
    public Task<bool> MigrateSchemaWithLockAsync(string schemaName, CancellationToken cancellationToken = default)
        => RunUnderSchemaLockAsync(
            schemaName,
            () => migrator.MigrateSchemaAsync(schemaName, cancellationToken),
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> MigrateSchemaToTargetWithLockAsync(
        string schemaName, string targetMigration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetMigration))
            throw new ArgumentNullException(nameof(targetMigration), "Target migration cannot be null or empty");

        // Same lock key as the forward path on purpose: a targeted downgrade and a forward
        // migration must never run concurrently against the same schema.
        return RunUnderSchemaLockAsync(
            schemaName,
            () => targetedMigrator.MigrateSchemaToTargetAsync(schemaName, targetMigration, cancellationToken),
            cancellationToken);
    }

    private async Task<bool> RunUnderSchemaLockAsync(
        string schemaName, Func<Task> migrate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new ArgumentNullException(nameof(schemaName), "Schema name cannot be null or empty");

        var lockKey = $"{LockKeyPrefix}:{schemaName}";

        try
        {
            // Try to acquire distributed lock for this schema
            var lockOutcome = await lockService.ExecuteWithLockAsync(
                lockKey,
                migrate,
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

