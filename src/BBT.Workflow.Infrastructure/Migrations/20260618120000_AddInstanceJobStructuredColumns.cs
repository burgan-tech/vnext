using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds the structured projection columns <c>JobType</c> and <c>TransitionKey</c> to
    /// <c>InstanceJobs</c>, plus a partial composite index supporting state-scoped job
    /// cancellation (filter active jobs by <c>InstanceId</c> + <c>JobType</c> + <c>TransitionKey</c>).
    ///
    /// <para>
    /// These columns project the structured <c>JobName</c> (URN-style: <c>vnext.job.v1.{type}.{instanceId}[.{segment}]</c>)
    /// so cancellation/resolution no longer depends on fragile job-name suffix parsing.
    /// Existing rows are backfilled with <c>JobType = 0</c> (Unknown) and a null transition key;
    /// the cancellation service keeps a transitional suffix-based fallback for those legacy rows.
    /// </para>
    /// </summary>
    public partial class AddInstanceJobStructuredColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JobType",
                schema: "public",
                table: "InstanceJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TransitionKey",
                schema: "public",
                table: "InstanceJobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_Active_Instance_Type_Key",
                schema: "public",
                table: "InstanceJobs",
                columns: new[] { "InstanceId", "JobType", "TransitionKey" },
                filter: "\"IsActive\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstanceJobs_Active_Instance_Type_Key",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "JobType",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropColumn(
                name: "TransitionKey",
                schema: "public",
                table: "InstanceJobs");
        }
    }
}
