using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddInstancePerfIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InstanceTransitions_Instance_StartedAt",
                schema: "public",
                table: "InstanceTransitions",
                columns: new[] { "InstanceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InstanceTransitions_StartedAt_Brin",
                schema: "public",
                table: "InstanceTransitions",
                column: "StartedAt")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_CurrentState_Status",
                schema: "public",
                table: "Instances",
                columns: new[] { "CurrentState", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_Active_Domain_CreatedAt",
                schema: "public",
                table: "InstanceJobs",
                columns: new[] { "Domain", "CreatedAt" },
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceJobs_Active_Flow_CreatedAt",
                schema: "public",
                table: "InstanceJobs",
                columns: new[] { "FlowName", "CreatedAt" },
                filter: "\"IsActive\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstanceTransitions_Instance_StartedAt",
                schema: "public",
                table: "InstanceTransitions");

            migrationBuilder.DropIndex(
                name: "IX_InstanceTransitions_StartedAt_Brin",
                schema: "public",
                table: "InstanceTransitions");

            migrationBuilder.DropIndex(
                name: "IX_Instances_CurrentState_Status",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropIndex(
                name: "IX_InstanceJobs_Active_Domain_CreatedAt",
                schema: "public",
                table: "InstanceJobs");

            migrationBuilder.DropIndex(
                name: "IX_InstanceJobs_Active_Flow_CreatedAt",
                schema: "public",
                table: "InstanceJobs");
        }
    }
}
