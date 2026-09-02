using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddSubflowSettlementMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SettledAt",
                schema: "public",
                table: "InstancesCorrelations",
                type: "timestamp with time zone",
                nullable: true,
                comment: "Durable terminal settlement marker; set after blocking parent resume completes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettledAt",
                schema: "public",
                table: "InstancesCorrelations");
        }
    }
}
