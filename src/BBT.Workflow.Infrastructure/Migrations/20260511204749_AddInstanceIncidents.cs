using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Incidents",
                schema: "public",
                table: "Instances",
                type: "jsonb",
                nullable: true,
                defaultValueSql: "'[]'::jsonb");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Instances_HasActiveIncident",
                schema: "public",
                table: "Instances");

            migrationBuilder.Sql("""
                                 ALTER TABLE "Instances" DROP COLUMN IF EXISTS "HasActiveIncident";
                                 """);

            migrationBuilder.DropColumn(
                name: "Incidents",
                schema: "public",
                table: "Instances");
        }
    }
}
