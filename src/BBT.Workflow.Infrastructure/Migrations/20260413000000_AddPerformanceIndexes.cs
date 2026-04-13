using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // InstanceTransitions: Partial index for incomplete (active) transitions.
            // Covers GetLatestIncompleteAsync: WHERE InstanceId=X AND FinishedAt IS NULL ORDER BY StartedAt DESC
            // Uses partial filter so only in-progress transitions are indexed — keeps it small and fast.
            migrationBuilder.Sql(
                @"CREATE INDEX ""IX_InstanceTransitions_Active"" ON ""InstanceTransitions"" (""InstanceId"" ASC, ""StartedAt"" DESC) WHERE ""FinishedAt"" IS NULL");

            // InstanceTransitions: Partial index for completed manual transitions.
            // Covers GetLastCompletedManualTransitionAsync: WHERE InstanceId=X AND FinishedAt IS NOT NULL AND TriggerType=1
            migrationBuilder.CreateIndex(
                name: "IX_InstanceTransitions_Manual_Completed",
                table: "InstanceTransitions",
                columns: new[] { "InstanceId", "TriggerType", "FinishedAt" },
                filter: "\"FinishedAt\" IS NOT NULL");

            // InstanceTasks: Composite index covering all status-based task queries.
            // Covers GetCompletedTaskIdsAsync, GetTaskIdsByStatusAsync, GetSuccessfulTaskIdsAsync, GetByTransitionIdAsync.
            // The leading TransitionId column supersedes the existing simple IX_InstanceTasks_TransitionId for these queries.
            migrationBuilder.CreateIndex(
                name: "IX_InstanceTasks_TransitionId_Status_BusinessStatus",
                table: "InstanceTasks",
                columns: new[] { "TransitionId", "Status", "BusinessStatus" });

            // InstanceJobs: Partial composite index for active job lookups.
            // Covers GetListActiveAsync (InstanceId only) and MarkAsProcessedAsync / AnyActiveByJobNameAsync (InstanceId+JobName).
            // IX_InstanceJobs_InstanceId was intentionally removed (migration 20250916); this replaces it more efficiently.
            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_Active",
                table: "InstanceJobs",
                columns: new[] { "InstanceId", "JobName" },
                filter: "\"IsActive\" = true");

            // Instances: Partial index for active instance timeline pagination.
            // Covers GetActiveDataListSinceAsync: WHERE Status='A' AND ModifiedAt >= @since ORDER BY Id
            migrationBuilder.CreateIndex(
                name: "IX_Instances_Active_ModifiedAt",
                table: "Instances",
                columns: new[] { "ModifiedAt", "Id" },
                filter: "\"Status\" = 'A'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"DROP INDEX IF EXISTS ""IX_InstanceTransitions_Active""");

            migrationBuilder.DropIndex(
                name: "IX_InstanceTransitions_Manual_Completed",
                table: "InstanceTransitions");

            migrationBuilder.DropIndex(
                name: "IX_InstanceTasks_TransitionId_Status_BusinessStatus",
                table: "InstanceTasks");

            migrationBuilder.DropIndex(
                name: "IX_InstanceJobs_Active",
                table: "InstanceJobs");

            migrationBuilder.DropIndex(
                name: "IX_Instances_Active_ModifiedAt",
                table: "Instances");
        }
    }
}
