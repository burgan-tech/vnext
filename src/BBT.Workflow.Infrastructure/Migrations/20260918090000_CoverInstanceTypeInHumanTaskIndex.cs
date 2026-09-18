using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Rebuilds <c>IX_Instances_HumanTaskV2</c> with <c>Type</c> in its <c>INCLUDE</c> payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The candidate scan started selecting <c>Type</c>, because a SubProcess row has to be
    /// addressed by its own id rather than by the business key it inherited from the case that
    /// spawned it. <c>Type</c> already appeared in the index's partial predicate — but a partial
    /// index's predicate columns are <b>not retrievable</b> from the index, so selecting it without
    /// including it turns the Index Only Scan into an index scan with a heap fetch per row.
    /// Measured before the fix: <c>Heap Fetches: 0</c>; after adding the column to the SELECT but
    /// not the index, every matching row fetches.
    /// </para>
    /// <para>
    /// Drop-then-create rather than an in-place change: PostgreSQL cannot alter an index's INCLUDE
    /// list. The window between the two statements is inside one migration transaction, so no query
    /// ever observes the table without the index — but the build is a build, with the same
    /// per-schema SHARE lock as the original.
    /// </para>
    /// </remarks>
    [DbContext(typeof(WorkflowDbContext))]
    [Migration("20260918090000_CoverInstanceTypeInHumanTaskIndex")]
    public partial class CoverInstanceTypeInHumanTaskIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Instances_HumanTaskV2";""");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Instances_HumanTaskV2\""
                + " ON \"Instances\" (\"CreatedAt\" DESC, \"Id\") INCLUDE (\"Key\", \"Type\")"
                + " WHERE " + HumanTaskQuerySql.Predicate + ";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Instances_HumanTaskV2";""");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Instances_HumanTaskV2\""
                + " ON \"Instances\" (\"CreatedAt\" DESC, \"Id\") INCLUDE (\"Key\")"
                + " WHERE " + HumanTaskQuerySql.Predicate + ";");
        }
    }
}
