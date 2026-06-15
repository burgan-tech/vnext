using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Drops legacy indexes that have been fully subsumed by the partial/covering indexes
    /// introduced in <see cref="OptimizeInstancesIndexes"/>.
    ///
    /// <para>Decision basis (pg_stat_user_indexes snapshot, post-OptimizeInstancesIndexes soak):</para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>IX_InstancesCorrelations_Performance</c> (ParentInstanceId, IsCompleted, SubFlowType) —
    /// the planner kept selecting it over <c>IX_InstancesCorrelations_ActiveByParent_Covering</c>
    /// because it is a smaller, full B-tree that can resolve the entire WHERE clause via index
    /// condition. Dropping it forces the planner onto the new partial covering index, which
    /// additionally supplies all SubFlow* columns via INCLUDE so the runtime hot-path becomes
    /// an Index Only Scan.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>IX_InstanceTransitions_InstanceId</c> — EF-Core auto FK index, observed scan count
    /// is zero in production stats. Both <c>IX_InstanceTransitions_Incomplete</c> and
    /// <c>IX_InstanceTransitions_CompletedManual</c> share <c>InstanceId</c> as the leftmost
    /// column, so any FK-style lookup the planner might prefer is already covered by a
    /// partial index union (or by the table itself when the partial sets miss).
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>IX_InstanceTasks_TransitionId</c> — EF-Core auto FK index, partly subsumed by the
    /// new <c>IX_InstanceTasks_Transition_Status_Covering</c> which uses <c>(TransitionId, Status)</c>
    /// as its leftmost columns. Lookups that only filter on <c>TransitionId</c> still get a
    /// valid Index (Only) Scan via the leftmost prefix.
    /// </description>
    /// </item>
    /// </list>
    ///
    /// <para>
    /// <c>IX_InstancesCorrelations_InstanceId</c> (shadow column index) is intentionally NOT
    /// dropped — see decision #1 in the optimization plan ("DEĞİŞİKLİK YAPMA").
    /// </para>
    ///
    /// <para>
    /// CONCURRENTLY is not used because the multi-schema migrator runs each migration inside
    /// its own transaction. <c>DROP INDEX</c> only takes a brief AccessExclusiveLock on the
    /// index itself; the table remains queryable.
    /// </para>
    /// </summary>
    public partial class CleanupRedundantInstanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstancesCorrelations_Performance",
                schema: "public",
                table: "InstancesCorrelations");

            // FK auto-indexes that EF Core generated implicitly. They are not declared in the
            // model snapshot, so DropIndex on them via the structured operation works (the
            // multi-schema generator rewrites schema "public" to the active tenant schema).
            migrationBuilder.DropIndex(
                name: "IX_InstanceTransitions_InstanceId",
                schema: "public",
                table: "InstanceTransitions");

            migrationBuilder.DropIndex(
                name: "IX_InstanceTasks_TransitionId",
                schema: "public",
                table: "InstanceTasks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_InstancesCorrelations_Performance",
                schema: "public",
                table: "InstancesCorrelations",
                columns: new[] { "ParentInstanceId", "IsCompleted", "SubFlowType" });
        }
    }
}
