using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class MoveInstanceIncidentsToTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The legacy jsonb column "Instances"."Incidents" is deliberately NOT dropped in this
            // release: it is the source of the follow-up BackfillInstanceIncidents migration and a
            // rollback safety net. It is no longer mapped by EF (nullable, default '[]'::jsonb, so
            // inserts that omit it keep working) and is removed by a later release's migration.
            //
            // The previous "HasActiveIncident" was a STORED generated column derived from that jsonb
            // (with a partial index). Neither was ever part of the EF model, so the scaffolder does not
            // know about them — drop both by hand before adding the real, aggregate-maintained column
            // of the same name. Unqualified names: MultiSchemaNpgsqlMigrationsSqlGenerator prepends
            // SET search_path for the target schema.
            migrationBuilder.Sql("""
                                 DROP INDEX IF EXISTS "IX_Instances_HasActiveIncident";
                                 ALTER TABLE "Instances" DROP COLUMN IF EXISTS "HasActiveIncident";
                                 """);

            migrationBuilder.AddColumn<bool>(
                name: "HasActiveIncident",
                schema: "public",
                table: "Instances",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "InstanceIncidents",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Transition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Task = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    StackTrace = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ErrorLayer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    BoundaryAction = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BoundaryLevel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsResolved = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceIncidents", x => x.Id);
                    // principalSchema stays null on purpose — the repo convention for every child
                    // table since 20250523074013_Initial. MultiSchemaNpgsqlMigrationsSqlGenerator
                    // rewrites CreateTableOperation.Schema but NOT the nested foreign keys, so an
                    // explicit "public" here would pin every flow schema's FK to public."Instances"
                    // and the backfill would fail with 23503 on the first row.
                    table.ForeignKey(
                        name: "FK_InstanceIncidents_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: null,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Instances_HasActiveIncident",
                schema: "public",
                table: "Instances",
                column: "HasActiveIncident",
                filter: "\"HasActiveIncident\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceIncidents_InstanceId_CreatedAt",
                schema: "public",
                table: "InstanceIncidents",
                columns: new[] { "InstanceId", "CreatedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceIncidents",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "IX_Instances_HasActiveIncident",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "HasActiveIncident",
                schema: "public",
                table: "Instances");

            // The jsonb column was never dropped by Up, so only the generated column and its partial
            // index (from 20260511204749_AddInstanceIncidents) need to be restored.
            migrationBuilder.Sql("""
                                 ALTER TABLE "Instances"
                                 ADD COLUMN "HasActiveIncident" boolean
                                 GENERATED ALWAYS AS (
                                     "Incidents" IS NOT NULL
                                     AND jsonb_path_exists("Incidents", '$[*] ? (@.isResolved == false)')
                                 ) STORED;
                                 """);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_HasActiveIncident",
                schema: "public",
                table: "Instances",
                column: "HasActiveIncident",
                filter: "\"HasActiveIncident\" = true");
        }
    }
}
