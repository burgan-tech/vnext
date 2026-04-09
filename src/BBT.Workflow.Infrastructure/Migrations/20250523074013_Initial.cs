using System;
using System.Collections.Generic;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // migrationBuilder.EnsureSchema(
            //     name: "public");

            migrationBuilder.CreateTable(
                name: "Instances",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Flow = table.Column<string>(type: "text", nullable: false),
                    CurrentState = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstanceJobs",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobName = table.Column<string>(type: "character varying(125)", maxLength: 125, nullable: false),
                    JobId = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    FlowName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Domain = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpressionValue = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanceJobs_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: null,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstancesCorrelations",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentState = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubFlowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstancesCorrelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstancesCorrelations_Instances_ParentInstanceId",
                        column: x => x.ParentInstanceId,
                        principalSchema: null,
                        principalTable: "Instances",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_InstancesCorrelations_Instances_SubFlowInstanceId",
                        column: x => x.SubFlowInstanceId,
                        principalSchema: null,
                        principalTable: "Instances",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "InstancesData",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ETag = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    EnteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstancesData", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstancesData_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: null,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstanceTransitions",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransitionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FromState = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ToState = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    Body = table.Column<string>(type: "jsonb", nullable: false),
                    Header = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanceTransitions_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: null,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstanceTasks",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TransitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    FaultedTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    Request = table.Column<string>(type: "jsonb", nullable: false),
                    Response = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanceTasks_InstanceTasks_FaultedTaskId",
                        column: x => x.FaultedTaskId,
                        principalSchema: null,
                        principalTable: "InstanceTasks",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_InstanceTasks_InstanceTransitions_TransitionId",
                        column: x => x.TransitionId,
                        principalSchema: null,
                        principalTable: "InstanceTransitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstanceActions",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Detail = table.Column<string>(type: "jsonb", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    Status = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanceActions_InstanceTasks_TaskId",
                        column: x => x.TaskId,
                        principalSchema: null,
                        principalTable: "InstanceTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstanceActions_TaskId",
                schema: "public",
                table: "InstanceActions",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_InstanceId",
                schema: "public",
                table: "InstanceJobs",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_JobId",
                schema: "public",
                table: "InstanceJobs",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstancesCorrelations_ParentInstanceId",
                schema: "public",
                table: "InstancesCorrelations",
                column: "ParentInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_InstancesCorrelations_SubFlowInstanceId",
                schema: "public",
                table: "InstancesCorrelations",
                column: "SubFlowInstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstancesData_InstanceId",
                schema: "public",
                table: "InstancesData",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceTasks_FaultedTaskId",
                schema: "public",
                table: "InstanceTasks",
                column: "FaultedTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceTasks_TransitionId",
                schema: "public",
                table: "InstanceTasks",
                column: "TransitionId");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceTransitions_InstanceId",
                schema: "public",
                table: "InstanceTransitions",
                column: "InstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceActions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "InstanceJobs",
                schema: "public");

            migrationBuilder.DropTable(
                name: "InstancesCorrelations",
                schema: "public");

            migrationBuilder.DropTable(
                name: "InstancesData",
                schema: "public");

            migrationBuilder.DropTable(
                name: "InstanceTasks",
                schema: "public");

            migrationBuilder.DropTable(
                name: "InstanceTransitions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "Instances",
                schema: "public");
        }
    }
}
