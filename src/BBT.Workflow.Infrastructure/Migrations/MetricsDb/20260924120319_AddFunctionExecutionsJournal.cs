using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations.MetricsDb
{
    /// <summary>
    /// Creates the domain-wide function-execution journal (vnext-client-sdk-core#60, items C1/D) in the
    /// fixed <c>sys_metrics</c> schema: one row per domain-function invocation, with a composite
    /// <c>(FunctionKey, InvokedAt)</c> index serving the per-function paged metrics query. Not a
    /// per-flow table — a function can run domain-scoped with no flow, so <c>Workflow</c>/<c>InstanceId</c>
    /// are nullable; <c>TraceId</c> links a row to its APM/ELK trace. The single migration for the issue.
    /// </summary>
    public partial class AddFunctionExecutionsJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sys_metrics");

            migrationBuilder.CreateTable(
                name: "FunctionExecutions",
                schema: "sys_metrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FunctionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FunctionVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scope = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    Workflow = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FromCache = table.Column<bool>(type: "boolean", nullable: false),
                    TraceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedByBehalfOf = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunctionExecutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FunctionExecutions_FunctionKey_InvokedAt",
                schema: "sys_metrics",
                table: "FunctionExecutions",
                columns: new[] { "FunctionKey", "InvokedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FunctionExecutions",
                schema: "sys_metrics");
        }
    }
}
