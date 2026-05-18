using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds a partial composite index on <c>InstanceJobs</c> for the pipeline hot-path
    /// queries that filter by <c>InstanceId</c> + <c>JobName</c> + <c>IsActive = true</c>.
    ///
    /// <para>
    /// Three repository methods benefit from this index:
    /// <list type="bullet">
    ///   <item><c>GetListActiveAsync</c>: <c>WHERE InstanceId = @id AND IsActive = true</c></item>
    ///   <item><c>MarkAsProcessedAsync</c>: <c>WHERE InstanceId = @id AND JobName = @name AND IsActive = true</c></item>
    ///   <item><c>AnyActiveByJobNameAsync</c>: <c>WHERE InstanceId = @id AND JobName = @name AND IsActive = true</c></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The original FK index <c>IX_InstanceJobs_InstanceId</c> was dropped in
    /// <c>Removed_InstanceJob_InstanceFK</c> (20250916), leaving these queries without
    /// any usable index. The partial filter (<c>IsActive = true</c>) keeps the index
    /// compact since most jobs are short-lived and quickly marked as processed.
    /// </para>
    /// </summary>
    public partial class AddInstanceJobsActiveIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_Active_Instance_JobName",
                schema: "public",
                table: "InstanceJobs",
                columns: new[] { "InstanceId", "JobName" },
                filter: "\"IsActive\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstanceJobs_Active_Instance_JobName",
                schema: "public",
                table: "InstanceJobs");
        }
    }
}
