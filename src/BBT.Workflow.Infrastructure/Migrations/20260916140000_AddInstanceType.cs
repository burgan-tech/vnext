using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds <c>"Instances"."Type"</c> — how the instance was STARTED: <c>'R'</c> root, <c>'S'</c>
    /// SubFlow child, <c>'P'</c> SubProcess child. Written once by the aggregate at creation and
    /// never updated, so the origin stays answerable long after the instance has finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely additive. The <c>parent.*</c> ExtraProperties contracts and everything reading them
    /// (<c>Instance.IsSubFlow</c> / <c>IsSubItem</c>) are untouched; this column exists because
    /// <c>ExtraProperties</c> is a <c>text</c> column holding JSON and cannot be filtered, sorted
    /// or grouped without a <c>::jsonb</c> cast.
    /// </para>
    /// <para>
    /// <c>ADD COLUMN … NOT NULL DEFAULT 'R'</c> is catalog-only on PostgreSQL 11+ — no table
    /// rewrite — and the backfill then touches only the child rows, since the default already
    /// answers for every root. Table names inside <c>Sql(...)</c> are UNQUALIFIED:
    /// <c>MultiSchemaNpgsqlMigrationsSqlGenerator</c> prepends the target schema's
    /// <c>SET search_path</c> and the migration runs once per schema.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// The two attributes are load-bearing: this migration is hand-written and has no
    /// <c>.Designer.cs</c>, so nothing else tells EF which context it belongs to or what its
    /// migration id is — without them it is silently never discovered, and the migrator still
    /// reports success. Same shape as <c>20260908120000_AddInstanceListOrderIndex</c>.
    /// </remarks>
    [DbContext(typeof(WorkflowDbContext))]
    [Migration("20260916140000_AddInstanceType")]
    public partial class AddInstanceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Type",
                schema: "public",
                table: "Instances",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "R");

            // Derive from the parent block: a non-empty `parent.id` means the row was started as a
            // child, and `parent.flowtype` says which kind.
            //
            // `parent.flowtype` ALONE is not a discriminator, which is why `parent.id` gates the
            // whole predicate: Instance.SetInfoMetadata TryAdds the instance's OWN workflow type
            // code into that key when it is absent, so a root started on a definition whose type is
            // 'S' carries "parent.flowtype":"S" with no parent at all.
            //
            // "ExtraProperties" is a text column holding JSON and is cast unconditionally below.
            // That is safe, and provably so: IX_Instances_HumanTask's partial-index filter already
            // casts ("ExtraProperties")::jsonb for EVERY row of the table, so a row that could not
            // be cast could not exist — the index would have failed to build. Do NOT "harden" this
            // with a LIKE '{%' guard in the same AND chain: PostgreSQL does not guarantee
            // short-circuit evaluation, so such a guard buys nothing and only implies an invariant
            // that does not hold. jsonb_exists(...) is the function spelling of the `?` operator,
            // used so no driver can mistake a `?` for a parameter placeholder.
            //
            // Deliberately a PARTIAL update. Rewriting every row with a CASE expression would turn a
            // child-subset write into a full-table rewrite for no gain — the column default has
            // already answered for the roots.
            migrationBuilder.Sql(
                """
                UPDATE "Instances"
                SET "Type" = "ExtraProperties"::jsonb ->> 'parent.flowtype'
                WHERE jsonb_exists("ExtraProperties"::jsonb, 'parent.id')
                  AND coalesce("ExtraProperties"::jsonb ->> 'parent.id', '') NOT IN
                      ('', '00000000-0000-0000-0000-000000000000')
                  AND "ExtraProperties"::jsonb ->> 'parent.flowtype' IN ('S', 'P')
                  AND "Type" IS DISTINCT FROM "ExtraProperties"::jsonb ->> 'parent.flowtype';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                schema: "public",
                table: "Instances");
        }
    }
}
