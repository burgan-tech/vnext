using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class InstanceTaskExecutionJournalKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionKey",
                schema: "public",
                table: "InstanceTasks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_InstanceTasks_ExecutionKey",
                schema: "public",
                table: "InstanceTasks",
                column: "ExecutionKey",
                unique: true,
                filter: "\"ExecutionKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InstanceTasks_ExecutionKey",
                schema: "public",
                table: "InstanceTasks");

            migrationBuilder.DropColumn(
                name: "ExecutionKey",
                schema: "public",
                table: "InstanceTasks");
        }
    }
}
