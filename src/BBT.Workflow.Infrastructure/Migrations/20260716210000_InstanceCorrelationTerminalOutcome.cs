using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class InstanceCorrelationTerminalOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TerminalOutcome",
                schema: "public",
                table: "InstancesCorrelations",
                type: "integer",
                nullable: true,
                comment: "Completed=1, Faulted=2, Canceled=3; null for legacy rows");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TerminalOutcome",
                schema: "public",
                table: "InstancesCorrelations");
        }
    }
}
