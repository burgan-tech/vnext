using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds <c>"Instances"."EffectiveStatus"</c> — the client-visible status (the deepest active
    /// SubFlow's status, else the instance's own). Fingerprint material for the state function: an
    /// accept that reserves a SubFlow chain flips only the LEAF's status, so without this column an
    /// ancestor's row is bit-identical before and after, and a cached state response stays valid
    /// across exactly the transition it must not survive.
    /// </summary>
    /// <remarks>
    /// The column default is 'A' only so the ADD COLUMN can be non-null; every existing row is then
    /// backfilled from its own <c>Status</c>, which is correct for every instance not currently
    /// sitting inside an active SubFlow. Those carry their own status until the next flip or upward
    /// notification restamps them — a transient over-fresh value, never a stale Busy, and it cannot
    /// produce a wrong response because the column is never served. Table names are unqualified:
    /// <c>MultiSchemaNpgsqlMigrationsSqlGenerator</c> prepends the target schema's
    /// <c>SET search_path</c> and the migration runs once per schema.
    /// </remarks>
    public partial class AddInstanceEffectiveStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EffectiveStatus",
                schema: "public",
                table: "Instances",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "A");

            // Backfill from the row's own status. A parent currently inside an active SubFlow ends up
            // with its own status rather than the leaf's; the first flip or upward notification after
            // deploy restamps it.
            migrationBuilder.Sql(
                "UPDATE \"Instances\" SET \"EffectiveStatus\" = \"Status\" " +
                "WHERE \"EffectiveStatus\" IS DISTINCT FROM \"Status\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveStatus",
                schema: "public",
                table: "Instances");
        }
    }
}
