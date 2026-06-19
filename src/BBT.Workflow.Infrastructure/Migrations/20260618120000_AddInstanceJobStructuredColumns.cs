using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds the structured projection columns <c>JobType</c> and <c>TransitionKey</c> to
    /// <c>InstanceJobs</c>.
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
