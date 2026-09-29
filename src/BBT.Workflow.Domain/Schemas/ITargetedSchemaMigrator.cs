namespace BBT.Workflow.Schemas;

/// <summary>
/// Migrates a single schema to an explicit EF Core migration target — in either direction.
/// This is the primitive behind the DbMigrator <c>downgrade</c> command: unlike
/// <c>IMultiSchemaMigrator</c> (which always migrates to the assembly's latest), the target here is
/// a named migration, and EF converges to it — a schema above the target reverts (runs the
/// <c>Down()</c> methods newest-first), a schema below it catches up. The <c>Down()</c> code for a
/// migration exists only in the assembly that ships it, so a downgrade must always run from the
/// NEWER runtime image, before the deployment is rolled back to the older one.
/// </summary>
public interface ITargetedSchemaMigrator
{
    /// <summary>
    /// Reads a schema's migration state without changing anything: the applied head, what is still
    /// pending against this assembly, and any applied migrations this assembly does not know
    /// (the schema was migrated by a newer build — this binary cannot downgrade past them).
    /// A schema whose history table does not exist yet reports an empty applied set.
    /// </summary>
    Task<SchemaMigrationStatus> GetStatusAsync(string schema, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes what migrating <paramref name="schema"/> to <paramref name="targetMigration"/> would
    /// do, without executing it: the migrations to revert (newest first, the order their
    /// <c>Down()</c> methods run), the migrations to apply when the schema is below the target, and
    /// every destructive operation (table/column/schema drop) in the revert range — the caller
    /// gates those behind an explicit data-loss acknowledgment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The target does not resolve to a migration in this assembly, or it is
    /// <c>Migration.InitialDatabase</c> ("0", a full wipe — deliberately not supported).
    /// </exception>
    Task<SchemaMigrationPlan> PlanMigrationToTargetAsync(
        string schema, string targetMigration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Migrates the schema to the target migration (EF converge semantics — down or up as needed),
    /// using the same isolated context, per-schema history table and schema-rewriting SQL generator
    /// as the forward path. The caller is expected to have inspected the plan first.
    /// </summary>
    Task MigrateSchemaToTargetAsync(
        string schema, string targetMigration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates — without executing — the SQL that would take the schema from its current applied
    /// head to the target, schema-qualified exactly as the direct apply would emit it (history-table
    /// bookkeeping included). For review/dry-run purposes; the supported rollback path is the direct
    /// apply, which keeps the per-schema history consistent across the whole fleet.
    /// </summary>
    Task<string> GenerateScriptToTargetAsync(
        string schema, string targetMigration, CancellationToken cancellationToken = default);
}

/// <summary>Read-only migration state of one schema, as reported by <see cref="ITargetedSchemaMigrator.GetStatusAsync"/>.</summary>
/// <param name="Schema">The (unformatted) schema name the query was made for.</param>
/// <param name="AppliedHead">The newest applied migration id, or null when none are applied.</param>
/// <param name="AppliedCount">How many migrations the schema has applied.</param>
/// <param name="PendingMigrations">Assembly migrations the schema has not applied yet, in apply order.</param>
/// <param name="UnknownAppliedMigrations">Applied migrations this assembly does not contain (schema is ahead of this build).</param>
public sealed record SchemaMigrationStatus(
    string Schema,
    string? AppliedHead,
    int AppliedCount,
    IReadOnlyList<string> PendingMigrations,
    IReadOnlyList<string> UnknownAppliedMigrations);

/// <summary>The computed effect of migrating one schema to an explicit target, before executing it.</summary>
/// <param name="Schema">The (unformatted) schema name the plan was computed for.</param>
/// <param name="TargetMigration">The resolved full migration id of the requested target.</param>
/// <param name="AppliedHead">The schema's current applied head, or null when none are applied.</param>
/// <param name="MigrationsToRevert">Migrations whose <c>Down()</c> will run, newest first (execution order). Empty when the schema is at or below the target.</param>
/// <param name="MigrationsToApply">Migrations whose <c>Up()</c> will run when the schema is below the target (converge-up), in apply order.</param>
/// <param name="DestructiveOperations">Human-readable list of table/column/schema drops contained in the revert range — data these reverts lose permanently.</param>
/// <param name="ModelBreakingOperations">
/// Revert operations that remove or reshape a table/column THIS build's compiled EF model maps.
/// Running the same runtime version against the downgraded schema fails on every read/write of the
/// affected entity (42703/42P01) — so these are only acceptable when the runtime is being rolled
/// back to an older version right after the command (the explicit rollback-intent flag).
/// </param>
/// <param name="UnknownAppliedMigrations">Applied migrations this assembly does not contain. Non-empty ⇒ this binary must refuse: only the newer build that shipped them can revert them.</param>
public sealed record SchemaMigrationPlan(
    string Schema,
    string TargetMigration,
    string? AppliedHead,
    IReadOnlyList<string> MigrationsToRevert,
    IReadOnlyList<string> MigrationsToApply,
    IReadOnlyList<string> DestructiveOperations,
    IReadOnlyList<string> ModelBreakingOperations,
    IReadOnlyList<string> UnknownAppliedMigrations)
{
    /// <summary>True when the plan changes nothing (schema already exactly at the target).</summary>
    public bool IsNoOp => MigrationsToRevert.Count == 0 && MigrationsToApply.Count == 0;
}
