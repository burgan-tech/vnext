using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Drops <c>IX_Instances_HumanTask</c>, the partial index that served the human-task list while
    /// its predicate still probed <c>ExtraProperties::jsonb</c> for <c>parent.id</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing reads it any more: the query now selects on the instance's own columns and is served
    /// by <c>IX_Instances_HumanTaskV2</c>, measured as an Index Only Scan with zero heap fetches.
    /// Keeping it is not free — a partial index evaluates its predicate per tuple on every INSERT
    /// and UPDATE, so every write to every flow schema was paying for a <c>text→jsonb</c> cast that
    /// no read benefits from, and for six <c>INCLUDE</c> columns that a <c>SELECT *</c> query could
    /// never reach.
    /// </para>
    /// <para>
    /// <b>This closes the rollback window.</b> The replacement sequence kept the old index alive so
    /// that reverting the predicate would still land on an indexed plan; after this migration a
    /// revert falls back to a sequential scan per flow schema. Dropped on an explicit decision to
    /// stop paying the write cost.
    /// </para>
    /// <para>
    /// The drop itself takes a brief ACCESS EXCLUSIVE lock per schema — far cheaper than the build,
    /// which is why the reverse direction is the expensive one: <c>Down</c> rebuilds the index and
    /// <c>CREATE INDEX CONCURRENTLY</c> is unavailable inside a migration transaction.
    /// </para>
    /// <para>
    /// Table names inside <c>Sql(...)</c> are UNQUALIFIED: <c>MultiSchemaNpgsqlMigrationsSqlGenerator</c>
    /// prepends the target schema's <c>SET search_path</c> and the migration runs once per schema.
    /// The two attributes are load-bearing — hand-written migrations have no <c>.Designer.cs</c>, so
    /// without them EF never discovers this and the migrator still reports success.
    /// </para>
    /// </remarks>
    [DbContext(typeof(WorkflowDbContext))]
    [Migration("20260917210000_DropLegacyHumanTaskIndex")]
    public partial class DropLegacyHumanTaskIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Instances_HumanTask";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_Instances_HumanTask"
                    ON "Instances" ("CreatedAt" DESC)
                    INCLUDE ("Key", "Flow", "FlowVersion", "CurrentState", "EffectiveState", "Status")
                    WHERE "Status" IN ('A','B') AND "EffectiveStateSubType" = 6
                      AND NOT ("ExtraProperties"::jsonb ? 'parent.id');
                """);
        }
    }
}
