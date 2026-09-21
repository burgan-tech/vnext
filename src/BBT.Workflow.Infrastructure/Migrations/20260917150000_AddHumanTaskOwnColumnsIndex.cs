using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Creates <c>IX_Instances_HumanTaskV2</c>, the partial index serving the human-task list once
    /// its predicate moves off the <c>::jsonb</c> probe and onto the native columns:
    /// <c>Type IN ('R','P')</c>, <c>Status IN ('A','B')</c>, <c>EffectiveStatus = 'A'</c>,
    /// <c>EffectiveStateSubType = 6</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Index-only release.</b> The query still runs the old predicate after this ships, so this
    /// index is unused until the code change lands and the old <c>IX_Instances_HumanTask</c> stays
    /// in place. That ordering is deliberate: DbMigrator is a separate job from the hosts, so the
    /// two cannot be atomic, and a predicate that arrives before its index degrades to a sequential
    /// scan on every flow schema. The old index is dropped in a later release so a rollback of the
    /// predicate still lands on an indexed plan.
    /// </para>
    /// <para>
    /// <b>The filter text must match the query's WHERE character for character.</b> The planner
    /// discharges partial-index applicability by proving implication over the predicate's parse
    /// tree; the code side inlines these same literals for that reason.
    /// </para>
    /// <para>
    /// <b>Covering, and only three columns wide.</b> The candidate scan selects exactly
    /// <c>Id, Key, CreatedAt</c>: the first two are the key, so only <c>Key</c> needs
    /// <c>INCLUDE</c>, and the index can answer the whole scan without touching the heap. The old
    /// index carried six INCLUDE columns that could never be reached, because its query was
    /// <c>SELECT *</c> — a covering payload is worth nothing unless the projection is narrower than
    /// the row. The key order <c>CreatedAt DESC, Id</c> also serves the ORDER BY without a sort and
    /// matches the direction <c>IX_Instances_CreatedAt_Id</c> already uses.
    /// </para>
    /// <para>
    /// <b>Operational cost.</b> <c>CREATE INDEX CONCURRENTLY</c> cannot be used here — a migration
    /// runs inside a transaction — so the build takes a SHARE lock and blocks writes to that flow
    /// for its duration, once per flow schema. Measure schema count and per-schema row counts
    /// before running this against a live database, and record the concurrent write-blocking width,
    /// not only the total wall clock.
    /// </para>
    /// <para>
    /// Table names inside <c>Sql(...)</c> are UNQUALIFIED:
    /// <c>MultiSchemaNpgsqlMigrationsSqlGenerator</c> prepends the target schema's
    /// <c>SET search_path</c> and the migration runs once per schema. The two attributes are
    /// load-bearing — this migration is hand-written and has no <c>.Designer.cs</c>, so without
    /// them EF never discovers it and the migrator still reports success.
    /// </para>
    /// </remarks>
    [DbContext(typeof(WorkflowDbContext))]
    [Migration("20260917150000_AddHumanTaskOwnColumnsIndex")]
    public partial class AddHumanTaskOwnColumnsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Instances_HumanTaskV2\""
                + " ON \"Instances\" (\"CreatedAt\" DESC, \"Id\") INCLUDE (\"Key\")"
                + " WHERE " + HumanTaskQuerySql.Predicate + ";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Instances_HumanTaskV2";""");
        }
    }
}
