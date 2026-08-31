using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds a BRIN index on <c>InstanceTasks.StartedAt</c> for the Monitor task-stats
    /// aggregation, which groups the table by task within a time window. Rows are inserted in
    /// <c>StartedAt</c> order, the physical correlation BRIN relies on, so the index stays a few
    /// pages regardless of table size while bounding the aggregation's scan to the window.
    /// </summary>
    public partial class AddInstanceTasksStartedAtBrinIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InstanceTasks_StartedAt_Brin",
                schema: "public",
                table: "InstanceTasks",
                column: "StartedAt")
                .Annotation("Npgsql:IndexMethod", "brin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstanceTasks_StartedAt_Brin",
                schema: "public",
                table: "InstanceTasks");
        }
    }
}
