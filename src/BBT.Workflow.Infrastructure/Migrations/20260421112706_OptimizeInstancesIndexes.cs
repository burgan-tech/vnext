using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Optimizes the Instances domain hot-path queries by introducing partial and covering
    /// indexes that match the runtime predicates exactly.
    ///
    /// <para>Notes:</para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// All <c>schema: "public"</c> values are rewritten to the active tenant schema by
    /// <see cref="BBT.Workflow.Schemas.MultiSchemaNpgsqlMigrationsSqlGenerator"/> at execution
    /// time, so the literal value here is just the placeholder for the current tenant.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// CONCURRENTLY is intentionally NOT used: the multi-schema migrator runs each
    /// migration inside its own transaction (per <c>ctx.Database.MigrateAsync</c>) which is
    /// incompatible with <c>CREATE INDEX CONCURRENTLY</c>. Accepting brief AccessExclusive
    /// locks keeps the migration consistent with the rest of the codebase.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// The legacy single-column indexes <c>IX_InstanceTransitions_InstanceId</c> and
    /// <c>IX_InstanceTasks_TransitionId</c> are intentionally kept. A follow-up clean-up
    /// migration will drop them after observing <c>pg_stat_user_indexes.idx_scan</c>.
    /// </description>
    /// </item>
    /// </list>
    /// </summary>
    public partial class OptimizeInstancesIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Replace the existing UX_InstancesData_Instance_IsLatest with a covering version.
            //    The unique partial filter (IsLatest = true) stays the same; we just attach the
            //    runtime-read meta columns via INCLUDE so most reads become index-only scans.
            //    Data (jsonb) is intentionally excluded to keep the index compact.
            migrationBuilder.DropIndex(
                name: "UX_InstancesData_Instance_IsLatest",
                schema: "public",
                table: "InstancesData");

            migrationBuilder.CreateIndex(
                name: "UX_InstancesData_Instance_IsLatest",
                schema: "public",
                table: "InstancesData",
                column: "InstanceId",
                unique: true,
                filter: "\"IsLatest\" = true")
                .Annotation("Npgsql:IndexInclude", new[] { "Version", "VersionNo", "HistorySequence", "ETag", "DataHash", "EnteredAt" });

            // 2. Add LastTouchedAt = COALESCE(ModifiedAt, CreatedAt) as a STORED GENERATED column.
            //    Enables index-friendly incremental scans for GetActiveDataListSinceAsync.
            //    The column is auto-populated for existing rows during ALTER TABLE; large tables
            //    will hold an AccessExclusiveLock for the duration of the rewrite.
            migrationBuilder.AddColumn<DateTime>(
                name: "LastTouchedAt",
                schema: "public",
                table: "Instances",
                type: "timestamp with time zone",
                nullable: false,
                computedColumnSql: "COALESCE(\"ModifiedAt\", \"CreatedAt\")",
                stored: true);

            // 3. InstancesCorrelations partial covering index for the runtime active subset.
            //    Pairs with WithDetailsAsync's Include(c => !c.IsCompleted) filter; the planner
            //    can satisfy this include via an index-only scan.
            migrationBuilder.CreateIndex(
                name: "IX_InstancesCorrelations_ActiveByParent_Covering",
                schema: "public",
                table: "InstancesCorrelations",
                column: "ParentInstanceId",
                filter: "\"IsCompleted\" = false")
                .Annotation("Npgsql:IndexInclude", new[] { "SubFlowType", "SubFlowInstanceId", "SubFlowDomain", "SubFlowName", "SubFlowVersion", "SubFlowCurrentState", "SubFlowStateChangedAt", "ParentState" });

            // 4. Tiny partial index dedicated to the blocking-subflow predicate
            //    (AnyActiveSubFlowByParentAsync / FindActiveSubFlowByParentAsync) evaluated
            //    on every transition.
            migrationBuilder.CreateIndex(
                name: "IX_InstancesCorrelations_ActiveBlockingSubFlow",
                schema: "public",
                table: "InstancesCorrelations",
                column: "ParentInstanceId",
                filter: "\"IsCompleted\" = false AND \"SubFlowType\" = 'S'");

            // 5. UNIQUE index for FindBySubInstanceIdAsync. Also enforces the 1-1 invariant
            //    between an InstanceCorrelation and its started SubFlow instance.
            migrationBuilder.CreateIndex(
                name: "UX_InstancesCorrelations_SubFlowInstanceId",
                schema: "public",
                table: "InstancesCorrelations",
                column: "SubFlowInstanceId",
                unique: true);

            // 6. Instances Status='A' partial index family.
            //    Runtime DB queries only ever filter by Status == InstanceStatus.Active.
            //    Busy is consumed in-memory; Faulted retries take a PK lookup.
            migrationBuilder.CreateIndex(
                name: "IX_Instances_Active_Id",
                schema: "public",
                table: "Instances",
                column: "Id",
                filter: "\"Status\" = 'A'");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Active_Key",
                schema: "public",
                table: "Instances",
                column: "Key",
                filter: "\"Status\" = 'A'");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Active_LastTouched_Id",
                schema: "public",
                table: "Instances",
                columns: new[] { "LastTouchedAt", "Id" },
                filter: "\"Status\" = 'A'");

            // 7. InstanceTransitions partial indexes for the two repository hot paths.
            //    Manual = TriggerType 0 (default).
            migrationBuilder.CreateIndex(
                name: "IX_InstanceTransitions_Incomplete",
                schema: "public",
                table: "InstanceTransitions",
                columns: new[] { "InstanceId", "StartedAt" },
                filter: "\"FinishedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceTransitions_CompletedManual",
                schema: "public",
                table: "InstanceTransitions",
                columns: new[] { "InstanceId", "FinishedAt" },
                filter: "\"FinishedAt\" IS NOT NULL AND \"TriggerType\" = 0");

            // 8. InstanceTasks covering index. Serves the three TaskId-projection queries
            //    (GetCompletedTaskIdsAsync, GetTaskIdsByStatusAsync, GetSuccessfulTaskIdsAsync)
            //    as index-only scans; GetByTransitionIdAsync (no Status filter) can also use
            //    it via the leftmost prefix.
            migrationBuilder.CreateIndex(
                name: "IX_InstanceTasks_Transition_Status_Covering",
                schema: "public",
                table: "InstanceTasks",
                columns: new[] { "TransitionId", "Status" })
                .Annotation("Npgsql:IndexInclude", new[] { "TaskId", "BusinessStatus", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstanceTasks_Transition_Status_Covering",
                schema: "public",
                table: "InstanceTasks");

            migrationBuilder.DropIndex(
                name: "IX_InstanceTransitions_CompletedManual",
                schema: "public",
                table: "InstanceTransitions");

            migrationBuilder.DropIndex(
                name: "IX_InstanceTransitions_Incomplete",
                schema: "public",
                table: "InstanceTransitions");

            migrationBuilder.DropIndex(
                name: "IX_Instances_Active_LastTouched_Id",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropIndex(
                name: "IX_Instances_Active_Key",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropIndex(
                name: "IX_Instances_Active_Id",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropIndex(
                name: "UX_InstancesCorrelations_SubFlowInstanceId",
                schema: "public",
                table: "InstancesCorrelations");

            migrationBuilder.DropIndex(
                name: "IX_InstancesCorrelations_ActiveBlockingSubFlow",
                schema: "public",
                table: "InstancesCorrelations");

            migrationBuilder.DropIndex(
                name: "IX_InstancesCorrelations_ActiveByParent_Covering",
                schema: "public",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "LastTouchedAt",
                schema: "public",
                table: "Instances");

            // Restore the original (non-covering) UX_InstancesData_Instance_IsLatest.
            migrationBuilder.DropIndex(
                name: "UX_InstancesData_Instance_IsLatest",
                schema: "public",
                table: "InstancesData");

            migrationBuilder.CreateIndex(
                name: "UX_InstancesData_Instance_IsLatest",
                schema: "public",
                table: "InstancesData",
                column: "InstanceId",
                unique: true,
                filter: "\"IsLatest\" = true");
        }
    }
}
