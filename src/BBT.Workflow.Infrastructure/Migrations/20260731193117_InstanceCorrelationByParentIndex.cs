using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class InstanceCorrelationByParentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InstancesCorrelations_ByParent",
                schema: "public",
                table: "InstancesCorrelations",
                column: "ParentInstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstancesCorrelations_ByParent",
                schema: "public",
                table: "InstancesCorrelations");
        }
    }
}
