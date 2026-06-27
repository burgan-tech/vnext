using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations.MessagingDb
{
    /// <inheritdoc />
    public partial class BackgroundJob_ArmingToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ArmingToken",
                schema: "sys_queues",
                table: "BackgroundJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArmingUntil",
                schema: "sys_queues",
                table: "BackgroundJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_ArmingUntil",
                schema: "sys_queues",
                table: "BackgroundJobs",
                column: "ArmingUntil",
                filter: "\"ArmingToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackgroundJobs_ArmingUntil",
                schema: "sys_queues",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "ArmingToken",
                schema: "sys_queues",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "ArmingUntil",
                schema: "sys_queues",
                table: "BackgroundJobs");
        }
    }
}
