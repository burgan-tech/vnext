using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AetherV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "BackgroundJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRunAt",
                table: "BackgroundJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetryCount",
                table: "BackgroundJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "BackgroundJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RunningSince",
                table: "BackgroundJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RunningToken",
                table: "BackgroundJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_Arming",
                table: "BackgroundJobs",
                columns: new[] { "Status", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_Running",
                table: "BackgroundJobs",
                columns: new[] { "Status", "RunningSince" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackgroundJobs_Arming",
                table: "BackgroundJobs");

            migrationBuilder.DropIndex(
                name: "IX_BackgroundJobs_Running",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "LastRunAt",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "MaxRetryCount",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "RunningSince",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "RunningToken",
                table: "BackgroundJobs");
        }
    }
}
