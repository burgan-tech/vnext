using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adjusts the <c>Instances</c> table indexes:
    ///
    /// <list type="number">
    /// <item>
    /// <description>
    /// Drops <c>IX_Instances_Active_Id</c>. The Primary Key already provides a B-tree on
    /// <c>Id</c>; every <c>Id</c>-based runtime query probes by PK without filtering on
    /// <c>Status</c>, so the planner never prefers the partial index. It only added write
    /// overhead.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// Adds <c>IX_Instances_Key</c>, a non-partial B-tree on <c>Key</c>. Both
    /// <c>FindByIdentifierAsync</c> and <c>FindByIdentifierAsReadOnlyAsync</c> probe by
    /// <c>Key</c> without a <c>Status</c> filter, so the existing partial
    /// <c>IX_Instances_Active_Key</c> (Status = 'A') cannot serve those lookups. The
    /// partial index is kept because it remains the smallest set for active-only reads.
    /// </description>
    /// </item>
    /// </list>
    ///
    /// <para>
    /// All <c>schema: "public"</c> values are rewritten to the active tenant schema by
    /// <see cref="BBT.Workflow.Schemas.MultiSchemaNpgsqlMigrationsSqlGenerator"/> at execution
    /// time, so the literal value here is just the placeholder for the current tenant.
    /// </para>
    ///
    /// <para>
    /// CONCURRENTLY is intentionally NOT used: the multi-schema migrator runs each
    /// migration inside its own transaction (per <c>ctx.Database.MigrateAsync</c>) which is
    /// incompatible with <c>CREATE INDEX CONCURRENTLY</c>. Both operations only take brief
    /// AccessExclusive locks on the index/table.
    /// </para>
    ///
    /// <para>
    /// The model snapshot also picks up cosmetic <c>ToTable(..., "public")</c> entries
    /// because the design-time factory was switched to a non-null schema in a prior
    /// commit. Those are intentionally omitted from this migration's <c>Up</c>/<c>Down</c>
    /// because <see cref="BBT.Workflow.Schemas.MultiSchemaNpgsqlMigrationsSqlGenerator"/>
    /// already routes DDL to the active tenant schema at apply time, making any
    /// <c>RenameTable</c> to/from the literal <c>"public"</c> a no-op (or worse, an error
    /// in non-public tenant schemas).
    /// </para>
    /// </summary>
    public partial class AdjustInstancesIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Instances_Active_Id",
                schema: "public",
                table: "Instances");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Key",
                schema: "public",
                table: "Instances",
                column: "Key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Instances_Key",
                schema: "public",
                table: "Instances");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Active_Id",
                schema: "public",
                table: "Instances",
                column: "Id",
                filter: "\"Status\" = 'A'");
        }
    }
}
