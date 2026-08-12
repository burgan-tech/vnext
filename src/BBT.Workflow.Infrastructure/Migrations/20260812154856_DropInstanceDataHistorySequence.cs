using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Drops <c>InstancesData.HistorySequence</c>. Its purpose was ordering not-yet-persisted
    /// same-Version rows in memory; with the InstanceData write service persisting every row
    /// immediately and assigning <c>VersionNo</c> under the per-instance FOR UPDATE lock,
    /// unpersisted multi-row states no longer exist and <c>VersionNo</c> is the single
    /// tie-breaker. The <c>UX_InstancesData_Instance_IsLatest</c> partial index is rebuilt
    /// without the column in its INCLUDE list; Down restores both.
    /// </summary>
    public partial class DropInstanceDataHistorySequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InstancesData_Instance_IsLatest",
                schema: "public",
                table: "InstancesData");

            migrationBuilder.DropColumn(
                name: "HistorySequence",
                schema: "public",
                table: "InstancesData");

            migrationBuilder.CreateIndex(
                name: "UX_InstancesData_Instance_IsLatest",
                schema: "public",
                table: "InstancesData",
                column: "InstanceId",
                unique: true,
                filter: "\"IsLatest\" = true")
                .Annotation("Npgsql:IndexInclude", new[] { "Version", "VersionNo", "ETag", "DataHash", "EnteredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InstancesData_Instance_IsLatest",
                schema: "public",
                table: "InstancesData");

            migrationBuilder.AddColumn<int>(
                name: "HistorySequence",
                schema: "public",
                table: "InstancesData",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "UX_InstancesData_Instance_IsLatest",
                schema: "public",
                table: "InstancesData",
                column: "InstanceId",
                unique: true,
                filter: "\"IsLatest\" = true")
                .Annotation("Npgsql:IndexInclude", new[] { "Version", "VersionNo", "HistorySequence", "ETag", "DataHash", "EnteredAt" });
        }
    }
}
