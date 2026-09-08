using Microsoft.Extensions.Options;

namespace BBT.Workflow.Schemas;

/// <summary>
/// Tunables for applying EF Core migrations to the per-flow PostgreSQL schemas (DbMigrator job,
/// Orchestration startup migration, runtime flow publish).
/// </summary>
/// <remarks>
/// Bound from the <c>SchemaMigration</c> configuration section. Data migrations that rewrite large
/// tables (e.g. the incident backfill) can run well past Npgsql's 30 s default command timeout; the
/// distributed lock held around a schema's migration must outlive the longest statement, otherwise a
/// second migrator could start the same schema mid-flight — hence the validation below.
/// </remarks>
public sealed class SchemaMigrationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SchemaMigration";

    /// <summary>
    /// Command timeout, in seconds, for every statement issued while migrating a schema
    /// (<c>MigrateAsync</c> and the messaging context migration). Default 600.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Expiry, in seconds, of the distributed lock held around a single schema's migration.
    /// Must be greater than <see cref="CommandTimeoutSeconds"/>. Default 900.
    /// </summary>
    public int LockExpirySeconds { get; set; } = 900;
}

/// <summary>Validates <see cref="SchemaMigrationOptions"/> at startup.</summary>
public sealed class SchemaMigrationOptionsValidator : IValidateOptions<SchemaMigrationOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SchemaMigrationOptions options)
    {
        if (options.CommandTimeoutSeconds <= 0)
            return ValidateOptionsResult.Fail("SchemaMigration:CommandTimeoutSeconds must be positive.");

        if (options.LockExpirySeconds <= options.CommandTimeoutSeconds)
            return ValidateOptionsResult.Fail(
                "SchemaMigration:LockExpirySeconds must be greater than CommandTimeoutSeconds; otherwise the schema lock can expire while a migration statement is still running.");

        return ValidateOptionsResult.Success;
    }
}
