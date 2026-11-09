using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceAgentTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AI agent states (issue #1105): run journal, tool calls, decisions and conversations. All four
            // live in each flow schema next to Instances and cascade with it. xmin is PostgreSQL's system
            // column (AgentSessions optimistic concurrency); the Npgsql generator does not create it.
            migrationBuilder.CreateTable(
                name: "AgentSessions",
                schema: "public",
                columns: table => new
                {
                    ScopeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Mode = table.Column<short>(type: "smallint", nullable: false),
                    Storage = table.Column<short>(type: "smallint", nullable: false),
                    AgentKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AgentVersion = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    InstructionsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SessionData = table.Column<byte[]>(type: "bytea", nullable: true),
                    ProviderConversationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EncryptionKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    MessageCount = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false),
                    OwnerInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessions", x => x.ScopeKey);
                    table.ForeignKey(
                        name: "FK_AgentSessions_Instances_OwnerInstanceId",
                        column: x => x.OwnerInstanceId,
                        principalSchema: null, // filled per flow schema by MultiSchemaNpgsqlMigrationsSqlGenerator (as AddInstanceSecrets)
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstanceAgentRuns",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExecutionKey = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    EntryNo = table.Column<int>(type: "integer", nullable: false),
                    AgentKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AgentDomain = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AgentVersion = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    RuntimeKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Attempts = table.Column<short>(type: "smallint", nullable: false),
                    ProviderResponseId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FinishReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Iterations = table.Column<int>(type: "integer", nullable: false),
                    ToolCallCount = table.Column<int>(type: "integer", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: false),
                    TotalTokens = table.Column<int>(type: "integer", nullable: false),
                    Request = table.Column<string>(type: "jsonb", nullable: true),
                    Response = table.Column<string>(type: "jsonb", nullable: true),
                    Transcript = table.Column<byte[]>(type: "bytea", nullable: true),
                    SessionScopeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SessionResumed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceAgentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanceAgentRuns_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: null, // filled per flow schema by MultiSchemaNpgsqlMigrationsSqlGenerator (as AddInstanceSecrets)
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstanceAgentDecisions",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<short>(type: "smallint", nullable: false),
                    Source = table.Column<short>(type: "smallint", nullable: false),
                    SelectedTransition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Candidates = table.Column<string>(type: "jsonb", nullable: false),
                    IsFallback = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceAgentDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanceAgentDecisions_InstanceAgentRuns_RunId",
                        column: x => x.RunId,
                        principalSchema: null, // filled per flow schema by MultiSchemaNpgsqlMigrationsSqlGenerator (as AddInstanceSecrets)
                        principalTable: "InstanceAgentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstanceAgentToolCalls",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Iteration = table.Column<int>(type: "integer", nullable: false),
                    CallId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    BindingRef = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Arguments = table.Column<string>(type: "jsonb", nullable: true),
                    Result = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    Error = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    InstanceTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceAgentToolCalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanceAgentToolCalls_InstanceAgentRuns_RunId",
                        column: x => x.RunId,
                        principalSchema: null, // filled per flow schema by MultiSchemaNpgsqlMigrationsSqlGenerator (as AddInstanceSecrets)
                        principalTable: "InstanceAgentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_ExpiresAt",
                schema: "public",
                table: "AgentSessions",
                column: "ExpiresAt",
                filter: "\"ExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_OwnerInstanceId",
                schema: "public",
                table: "AgentSessions",
                column: "OwnerInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceAgentDecisions_InstanceId",
                schema: "public",
                table: "InstanceAgentDecisions",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "UX_InstanceAgentDecisions_RunId",
                schema: "public",
                table: "InstanceAgentDecisions",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstanceAgentRuns_Instance_StartedAt",
                schema: "public",
                table: "InstanceAgentRuns",
                columns: new[] { "InstanceId", "StartedAt" },
                descending: new[] { false, true })
                .Annotation("Npgsql:IndexInclude", new[] { "State", "Status", "AgentKey", "TotalTokens" });

            migrationBuilder.CreateIndex(
                name: "UX_InstanceAgentRuns_ExecutionKey",
                schema: "public",
                table: "InstanceAgentRuns",
                column: "ExecutionKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstanceAgentToolCalls_InstanceId",
                schema: "public",
                table: "InstanceAgentToolCalls",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceAgentToolCalls_Run_Sequence",
                schema: "public",
                table: "InstanceAgentToolCalls",
                columns: new[] { "RunId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSessions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "InstanceAgentDecisions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "InstanceAgentToolCalls",
                schema: "public");

            migrationBuilder.DropTable(
                name: "InstanceAgentRuns",
                schema: "public");
        }
    }
}
