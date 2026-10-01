using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // x-encryption per-instance secrets (AES-256 key + hash salt), one row per instance, created by the
            // write funnel on the instance's first protected write, cascade-deleted with the instance.
            migrationBuilder.CreateTable(
                name: "InstanceSecrets",
                schema: "public",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EncryptionKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    HashSalt = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceSecrets", x => x.InstanceId);
                    table.ForeignKey(
                        name: "FK_InstanceSecrets_Instances_InstanceId",
                        column: x => x.InstanceId,
                        // Deliberately null (not "public"): MultiSchemaNpgsqlMigrationsSqlGenerator fills it from the
                        // table's own schema, so each flow schema's FK points at ITS Instances table — the same rule
                        // as MoveInstanceIncidentsToTable.
                        principalSchema: null,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceSecrets",
                schema: "public");
        }
    }
}
