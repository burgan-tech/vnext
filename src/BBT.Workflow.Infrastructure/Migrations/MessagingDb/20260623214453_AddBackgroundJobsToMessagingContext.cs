using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations.MessagingDb
{
    /// <inheritdoc />
    public partial class AddBackgroundJobsToMessagingContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundJobs",
                schema: "sys_queues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HandlerName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    JobName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ExpressionValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Payload = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    HandledTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    MaxRetryCount = table.Column<int>(type: "integer", nullable: false),
                    NextRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RunningSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RunningToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    CreatedByBehalfOf = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    ModifiedByBehalfOf = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DeletedBy = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_Arming",
                schema: "sys_queues",
                table: "BackgroundJobs",
                columns: new[] { "Status", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_HandlerName_Status",
                schema: "sys_queues",
                table: "BackgroundJobs",
                columns: new[] { "HandlerName", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_JobName",
                schema: "sys_queues",
                table: "BackgroundJobs",
                column: "JobName");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_Processing",
                schema: "sys_queues",
                table: "BackgroundJobs",
                columns: new[] { "Status", "HandledTime" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_Running",
                schema: "sys_queues",
                table: "BackgroundJobs",
                columns: new[] { "Status", "RunningSince" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundJobs",
                schema: "sys_queues");
        }
    }
}
