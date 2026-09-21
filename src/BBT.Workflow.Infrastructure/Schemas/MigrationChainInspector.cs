using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BBT.Workflow.Schemas;

/// <summary>
/// Read-only inspection and targeted-migration primitives over one <see cref="DbContext"/>'s
/// migration chain. Context-agnostic on purpose: the workflow chain uses it through the isolated
/// per-schema context <c>MultiSchemaMigrator</c> builds, and the messaging chain (fixed
/// <c>sys_queues</c> schema) uses it through the DI-registered <c>MessagingDbContext</c>.
/// Everything here reads the context's own migration services, so history-table naming and the
/// schema-rewriting SQL generator apply exactly as they do on the forward path.
/// </summary>
public static class MigrationChainInspector
{
    /// <summary>
    /// Resolves a user-supplied migration name or id against the context's migrations assembly.
    /// <c>Migration.InitialDatabase</c> ("0" — EF's revert-everything sentinel) is deliberately
    /// rejected: a full wipe is not a rollback.
    /// </summary>
    /// <exception cref="InvalidOperationException">Unknown target, or the "0" sentinel.</exception>
    public static string ResolveTargetMigrationId(DbContext context, string targetMigration)
    {
        if (string.IsNullOrWhiteSpace(targetMigration))
            throw new ArgumentNullException(nameof(targetMigration));

        if (targetMigration.Trim() == Migration.InitialDatabase)
            throw new InvalidOperationException(
                "Target '0' (revert every migration) would drop the whole schema content and is not supported. " +
                "Name the migration the schema should end up at.");

        var assembly = context.GetService<IMigrationsAssembly>();
        var resolved = assembly.Migrations.Keys.FirstOrDefault(id =>
            string.Equals(id, targetMigration, StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetMigrationName(id), targetMigration, StringComparison.OrdinalIgnoreCase));

        return resolved ?? throw new InvalidOperationException(
            $"Target migration '{targetMigration}' does not exist in this build's migrations assembly. " +
            "A downgrade must run from the image that CONTAINS the target (and everything above it) — " +
            "check the spelling, or run the command from the newer runtime image.");
    }

    /// <summary>
    /// Reads the applied/pending/unknown migration sets for the context's active schema. A missing
    /// history table (brand-new schema) reports an empty applied set instead of failing — the same
    /// reason the forward path avoids <c>GetPendingMigrationsAsync</c> before <c>MigrateAsync</c>.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Applied, IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown)>
        GetChainStateAsync(DbContext context, CancellationToken cancellationToken)
    {
        var assemblyIds = OrderedAssemblyMigrationIds(context);

        var historyRepository = context.GetService<IHistoryRepository>();
        List<string> applied = [];
        if (await historyRepository.ExistsAsync(cancellationToken))
        {
            applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        var appliedSet = applied.ToHashSet(StringComparer.Ordinal);
        var assemblySet = assemblyIds.ToHashSet(StringComparer.Ordinal);

        var pending = assemblyIds.Where(id => !appliedSet.Contains(id)).ToList();
        var unknown = applied.Where(id => !assemblySet.Contains(id)).ToList();
        return (applied, pending, unknown);
    }

    /// <summary>
    /// Computes the converge-to-target plan for the context's active schema: which migrations
    /// revert (newest first), which apply, and every destructive operation (table/column/schema
    /// drop) contained in the revert range.
    /// </summary>
    public static async Task<SchemaMigrationPlan> PlanAsync(
        DbContext context, string schema, string targetMigration, CancellationToken cancellationToken)
    {
        var targetId = ResolveTargetMigrationId(context, targetMigration);
        var (applied, _, unknown) = await GetChainStateAsync(context, cancellationToken);
        var assemblyIds = OrderedAssemblyMigrationIds(context);
        var assemblySet = assemblyIds.ToHashSet(StringComparer.Ordinal);
        var appliedSet = applied.ToHashSet(StringComparer.Ordinal);

        // Revert everything applied ABOVE the target (newest first — Down() execution order);
        // apply everything in the assembly AT OR BELOW the target that is not applied yet.
        var toRevert = applied
            .Where(id => string.CompareOrdinal(id, targetId) > 0 && assemblySet.Contains(id))
            .OrderByDescending(id => id, StringComparer.Ordinal)
            .ToList();
        var toApply = assemblyIds
            .Where(id => string.CompareOrdinal(id, targetId) <= 0 && !appliedSet.Contains(id))
            .ToList();

        return new SchemaMigrationPlan(
            schema,
            targetId,
            applied.Count > 0 ? applied[^1] : null,
            toRevert,
            toApply,
            FindDestructiveDownOperations(context, toRevert),
            FindModelBreakingDownOperations(context, toRevert),
            unknown);
    }

    /// <summary>
    /// Lists the operations in the given migrations' <c>Down()</c> bodies that permanently destroy
    /// data — table, column and schema drops. Raw SQL bodies are not classified (in this repo they
    /// recreate triggers/functions); structured drops are the gate the data-loss flag protects.
    /// </summary>
    public static IReadOnlyList<string> FindDestructiveDownOperations(
        DbContext context, IReadOnlyList<string> migrationIdsToRevert)
    {
        if (migrationIdsToRevert.Count == 0)
            return [];

        var assembly = context.GetService<IMigrationsAssembly>();
        var activeProvider = context.Database.ProviderName!;
        var destructive = new List<string>();

        foreach (var id in migrationIdsToRevert)
        {
            if (!assembly.Migrations.TryGetValue(id, out var typeInfo))
                continue;

            var migration = assembly.CreateMigration(typeInfo, activeProvider);
            foreach (var operation in migration.DownOperations)
            {
                var description = operation switch
                {
                    DropTableOperation op => $"DROP TABLE \"{op.Name}\"",
                    DropColumnOperation op => $"DROP COLUMN \"{op.Table}\".\"{op.Name}\"",
                    DropSchemaOperation op => $"DROP SCHEMA \"{op.Name}\"",
                    _ => null
                };
                if (description is not null)
                    destructive.Add($"{id}: {description}");
            }
        }

        return destructive;
    }

    /// <summary>
    /// Generates the SQL that would take the schema from its current applied head to the target —
    /// through the context's own SQL generator, so it is schema-qualified exactly like a direct
    /// apply, history-table bookkeeping included.
    /// </summary>
    public static async Task<string> GenerateScriptAsync(
        DbContext context, string targetMigration, CancellationToken cancellationToken)
    {
        var targetId = ResolveTargetMigrationId(context, targetMigration);
        var (applied, _, unknown) = await GetChainStateAsync(context, cancellationToken);
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"The schema has applied migrations this build does not contain ({string.Join(", ", unknown)}); " +
                "generate the script from the newer runtime image instead.");

        var from = applied.Count > 0 ? applied[^1] : Migration.InitialDatabase;
        return context.GetService<IMigrator>()
            .GenerateScript(from, targetId, MigrationsSqlGenerationOptions.Default);
    }

    /// <summary>
    /// Lists the revert operations that remove or reshape a table/column THIS build's compiled EF
    /// model maps. The migrator binary carries exactly the model the same-version runtime queries
    /// with, so this is a deterministic "will the runtime break if it stays deployed?" check:
    /// EF selects every mapped column on every read, so dropping (or renaming/re-typing) a mapped
    /// object fails the entity's reads and writes with 42703/42P01 at first touch. Additive Down
    /// operations (re-creating what the Up dropped), index/constraint changes and raw SQL bodies
    /// (trigger/function recreation in this repo) do not affect the model and are not flagged.
    /// </summary>
    public static IReadOnlyList<string> FindModelBreakingDownOperations(
        DbContext context, IReadOnlyList<string> migrationIdsToRevert)
    {
        if (migrationIdsToRevert.Count == 0)
            return [];

        // (table -> mapped columns) lookup for the CURRENT compiled model. Schema is ignored on
        // purpose: migration operations are authored against "public" and rewritten at apply time,
        // while the model is qualified with the active schema — the table name is the identity.
        var mappedTables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
            if (storeObject is null)
                continue;

            if (!mappedTables.TryGetValue(storeObject.Value.Name, out var columns))
                mappedTables[storeObject.Value.Name] = columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName(storeObject.Value);
                if (column is not null)
                    columns.Add(column);
            }
        }

        bool MapsTable(string? table) => table is not null && mappedTables.ContainsKey(table);
        bool MapsColumn(string? table, string? column) =>
            table is not null && column is not null
            && mappedTables.TryGetValue(table, out var cols) && cols.Contains(column);

        var assembly = context.GetService<IMigrationsAssembly>();
        var activeProvider = context.Database.ProviderName!;
        var breaking = new List<string>();

        foreach (var id in migrationIdsToRevert)
        {
            if (!assembly.Migrations.TryGetValue(id, out var typeInfo))
                continue;

            var migration = assembly.CreateMigration(typeInfo, activeProvider);
            foreach (var operation in migration.DownOperations)
            {
                var reason = operation switch
                {
                    DropTableOperation op when MapsTable(op.Name) =>
                        $"drops table \"{op.Name}\", which this build's runtime model maps",
                    DropColumnOperation op when MapsColumn(op.Table, op.Name) =>
                        $"drops column \"{op.Table}\".\"{op.Name}\", which this build's runtime model maps",
                    RenameTableOperation op when MapsTable(op.Name) && !string.Equals(op.NewName, op.Name, StringComparison.OrdinalIgnoreCase) =>
                        $"renames table \"{op.Name}\" (mapped by this build's runtime model) to \"{op.NewName}\"",
                    RenameColumnOperation op when MapsColumn(op.Table, op.Name) =>
                        $"renames column \"{op.Table}\".\"{op.Name}\" (mapped by this build's runtime model) to \"{op.NewName}\"",
                    // Conservative: reverting a type/nullability change on a mapped column can break
                    // the runtime's reads or writes even though nothing is dropped.
                    AlterColumnOperation op when MapsColumn(op.Table, op.Name) =>
                        $"reverts the definition of column \"{op.Table}\".\"{op.Name}\", which this build's runtime model maps",
                    _ => null
                };
                if (reason is not null)
                    breaking.Add($"{id}: {reason}");
            }
        }

        return breaking;
    }

    private static List<string> OrderedAssemblyMigrationIds(DbContext context) =>
        context.GetService<IMigrationsAssembly>().Migrations.Keys
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private static string GetMigrationName(string migrationId)
    {
        var separator = migrationId.IndexOf('_');
        return separator >= 0 && separator < migrationId.Length - 1
            ? migrationId[(separator + 1)..]
            : migrationId;
    }
}
