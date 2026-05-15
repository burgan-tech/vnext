using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddHumanTaskIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Instances_HumanTask",
                schema: "public",
                table: "Instances",
                column: "CreatedAt",
                descending: new[] { true },
                filter: "\"Status\" IN ('A','B') AND \"EffectiveStateSubType\" = 6 AND NOT (\"ExtraProperties\"::jsonb ? 'parent.id')")
                .Annotation("Npgsql:IndexInclude", new[] { "Key", "Flow", "FlowVersion", "CurrentState", "EffectiveState", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Instances_HumanTask",
                schema: "public",
                table: "Instances");
        }
    }
}
