using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds optimistic-concurrency revisioning to instances and durable admission/dispatch
    /// metadata to instance jobs.
    /// </summary>
    public partial class InstanceRevisionAndDurableJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The old filtered index allowed duplicate active admissions. Fail before any schema
            // changes so operators can reconcile legacy duplicates rather than receiving an opaque
            // CREATE UNIQUE INDEX failure after the table has already been altered.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM ""InstanceJobs""
        WHERE ""IsActive"" = TRUE
        GROUP BY ""InstanceId"", ""JobName""
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23505',
            MESSAGE = 'Cannot create IX_InstanceJobs_Active_Instance_JobName: duplicate active InstanceId/JobName rows exist';
    END IF;
END
$$;
");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                schema: "public",
                table: "Instances",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "AdmissionToken",
                schema: "public",
                table: "InstanceJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AdmittedRevision",
                schema: "public",
                table: "InstanceJobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                schema: "public",
                table: "InstanceJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DispatchStatus",
                schema: "public",
                table: "InstanceJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                schema: "public",
                table: "InstanceJobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorDetails",
                schema: "public",
                table: "InstanceJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "public",
                table: "InstanceJobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                schema: "public",
                table: "InstanceJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                schema: "public",
                table: "InstanceJobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessingAt",
                schema: "public",
                table: "InstanceJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessingLeaseUntil",
                schema: "public",
                table: "InstanceJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProcessingToken",
                schema: "public",
                table: "InstanceJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestFingerprint",
                schema: "public",
                table: "InstanceJobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Legacy jobs predate the durable dispatch state. Preserve their terminal/active
            // meaning before any dispatcher starts using the new column.
            migrationBuilder.Sql(@"
UPDATE ""InstanceJobs""
SET ""DispatchStatus"" = CASE
    WHEN ""IsActive"" THEN 0
    ELSE 3
END;
");

            migrationBuilder.DropIndex(
                name: "IX_InstanceJobs_Active_Instance_JobName",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_Active_Instance_JobName",
                schema: "public",
                table: "InstanceJobs",
                columns: new[] { "InstanceId", "JobName" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_Dispatch_NextAttemptAt",
                schema: "public",
                table: "InstanceJobs",
                columns: new[] { "DispatchStatus", "NextAttemptAt" },
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "UX_InstanceJobs_Instance_IdempotencyKey",
                schema: "public",
                table: "InstanceJobs",
                columns: new[] { "InstanceId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstanceJobs_Active_Instance_JobName",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropIndex(
                name: "IX_InstanceJobs_Dispatch_NextAttemptAt",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropIndex(
                name: "UX_InstanceJobs_Instance_IdempotencyKey",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "AdmissionToken",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "AdmittedRevision",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "DispatchStatus",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "ErrorDetails",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "Payload",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "ProcessingAt",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "ProcessingLeaseUntil",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "ProcessingToken",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "RequestFingerprint",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_Active_Instance_JobName",
                schema: "public",
                table: "InstanceJobs",
                columns: new[] { "InstanceId", "JobName" },
                filter: "\"IsActive\" = true");
        }
    }
}
