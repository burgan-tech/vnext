using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class Instance_LongPollAckToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LongPollAckToken",
                schema: "public",
                table: "Instances",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_LongPollAckToken",
                schema: "public",
                table: "Instances",
                column: "LongPollAckToken",
                filter: "\"LongPollAckToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Instances_LongPollAckToken",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "LongPollAckToken",
                schema: "public",
                table: "Instances");
        }
    }
}
