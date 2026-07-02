using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AetherBackgroundJobArming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // migrationBuilder.RenameTable(
            //     name: "BackgroundJobs",
            //     newName: "BackgroundJobs",
            //     newSchema: "public");
            migrationBuilder.AddColumn<Guid>(
                name: "ArmingToken",
                schema: "public",
                table: "BackgroundJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArmingUntil",
                schema: "public",
                table: "BackgroundJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_ArmingUntil",
                schema: "public",
                table: "BackgroundJobs",
                column: "ArmingUntil",
                filter: "\"ArmingToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackgroundJobs_ArmingUntil",
                schema: "public",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "ArmingToken",
                schema: "public",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "ArmingUntil",
                schema: "public",
                table: "BackgroundJobs");

            migrationBuilder.RenameTable(
                name: "BackgroundJobs",
                schema: "public",
                newName: "BackgroundJobs");
        }
    }
}
