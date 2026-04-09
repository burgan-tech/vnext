using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class Added_BackgroundJobAndChangeDateTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "MetaData",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ExpressionValue",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "Payload",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.RenameTable(
                name: "InstanceTransitions",
                schema: "public",
                newName: "InstanceTransitions");

            migrationBuilder.RenameTable(
                name: "InstanceTasks",
                schema: "public",
                newName: "InstanceTasks");

            migrationBuilder.RenameTable(
                name: "InstancesData",
                schema: "public",
                newName: "InstancesData");

            migrationBuilder.RenameTable(
                name: "InstancesCorrelations",
                schema: "public",
                newName: "InstancesCorrelations");

            migrationBuilder.RenameTable(
                name: "Instances",
                schema: "public",
                newName: "Instances");

            migrationBuilder.RenameTable(
                name: "InstanceJobs",
                schema: "public",
                newName: "InstanceJobs");

            migrationBuilder.RenameTable(
                name: "InstanceActions",
                schema: "public",
                newName: "InstanceActions");

            migrationBuilder.RenameColumn(
                name: "IsTriggered",
                table: "InstanceJobs",
                newName: "IsActive");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                table: "InstanceTransitions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FinishedAt",
                table: "InstanceTransitions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                table: "InstanceTasks",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FinishedAt",
                table: "InstanceTasks",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "EnteredAt",
                table: "InstancesData",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CompletedAt",
                table: "InstancesCorrelations",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InstanceId",
                table: "InstancesCorrelations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ModifiedAt",
                table: "Instances",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Instances",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CompletedAt",
                table: "Instances",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtraProperties",
                table: "Instances",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<DateTime>(
                name: "ModifiedAt",
                table: "InstanceJobs",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "JobName",
                table: "InstanceJobs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(125)",
                oldMaxLength: 125);
            
            migrationBuilder.AddColumn<Guid>(
                name: "JobId_tmp",
                schema: "public",
                table: "InstanceJobs",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()"
            );
            
            migrationBuilder.Sql($@"
                UPDATE ""InstanceJobs""
                SET ""JobId_tmp"" = CASE
                    WHEN ""JobId"" ~* '^[0-9a-fA-F-]{36}$' THEN ""JobId""::uuid
                    ELSE gen_random_uuid()
                END
            ");
            
            migrationBuilder.DropColumn(
                name: "JobId",
                schema: "public",
                table: "InstanceJobs"
            );
            
            migrationBuilder.RenameColumn(
                name: "JobId_tmp",
                schema: "public",
                table: "InstanceJobs",
                newName: "JobId"
            );

            migrationBuilder.AlterColumn<Guid>(
                name: "JobId",
                table: "InstanceJobs",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(400)",
                oldMaxLength: 400);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "InstanceJobs",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                table: "InstanceActions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FinishedAt",
                table: "InstanceActions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "BackgroundJobs",
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
                name: "IX_InstancesCorrelations_InstanceId",
                table: "InstancesCorrelations",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_HandlerName_Status",
                table: "BackgroundJobs",
                columns: new[] { "HandlerName", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_JobName",
                table: "BackgroundJobs",
                column: "JobName");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_Processing",
                table: "BackgroundJobs",
                columns: new[] { "Status", "HandledTime" });

            migrationBuilder.AddForeignKey(
                name: "FK_InstancesCorrelations_Instances_InstanceId",
                table: "InstancesCorrelations",
                column: "InstanceId",
                principalTable: "Instances",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InstancesCorrelations_Instances_InstanceId",
                table: "InstancesCorrelations");

            migrationBuilder.DropTable(
                name: "BackgroundJobs");

            migrationBuilder.DropIndex(
                name: "IX_InstancesCorrelations_InstanceId",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "InstanceId",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "ExtraProperties",
                table: "Instances");

            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.RenameTable(
                name: "InstanceTransitions",
                newName: "InstanceTransitions",
                newSchema: "public");

            migrationBuilder.RenameTable(
                name: "InstanceTasks",
                newName: "InstanceTasks",
                newSchema: "public");

            migrationBuilder.RenameTable(
                name: "InstancesData",
                newName: "InstancesData",
                newSchema: "public");

            migrationBuilder.RenameTable(
                name: "InstancesCorrelations",
                newName: "InstancesCorrelations",
                newSchema: "public");

            migrationBuilder.RenameTable(
                name: "Instances",
                newName: "Instances",
                newSchema: "public");

            migrationBuilder.RenameTable(
                name: "InstanceJobs",
                newName: "InstanceJobs",
                newSchema: "public");

            migrationBuilder.RenameTable(
                name: "InstanceActions",
                newName: "InstanceActions",
                newSchema: "public");

            migrationBuilder.RenameColumn(
                name: "IsActive",
                schema: "public",
                table: "InstanceJobs",
                newName: "IsTriggered");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                schema: "public",
                table: "InstanceTransitions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FinishedAt",
                schema: "public",
                table: "InstanceTransitions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                schema: "public",
                table: "InstanceTasks",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FinishedAt",
                schema: "public",
                table: "InstanceTasks",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "EnteredAt",
                schema: "public",
                table: "InstancesData",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CompletedAt",
                schema: "public",
                table: "InstancesCorrelations",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ModifiedAt",
                schema: "public",
                table: "Instances",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                schema: "public",
                table: "Instances",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CompletedAt",
                schema: "public",
                table: "Instances",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConcurrencyStamp",
                schema: "public",
                table: "Instances",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MetaData",
                schema: "public",
                table: "Instances",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<DateTime>(
                name: "ModifiedAt",
                schema: "public",
                table: "InstanceJobs",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "JobName",
                schema: "public",
                table: "InstanceJobs",
                type: "character varying(125)",
                maxLength: 125,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "JobId",
                schema: "public",
                table: "InstanceJobs",
                type: "character varying(400)",
                maxLength: 400,
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                schema: "public",
                table: "InstanceJobs",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<string>(
                name: "ExpressionValue",
                schema: "public",
                table: "InstanceJobs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                schema: "public",
                table: "InstanceJobs",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                schema: "public",
                table: "InstanceActions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FinishedAt",
                schema: "public",
                table: "InstanceActions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
