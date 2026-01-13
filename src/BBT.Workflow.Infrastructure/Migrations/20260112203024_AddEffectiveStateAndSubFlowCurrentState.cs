using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddEffectiveStateAndSubFlowCurrentState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubFlowCurrentState",
                table: "InstancesCorrelations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EffectiveState",
                table: "Instances",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_EffectiveState",
                table: "Instances",
                column: "EffectiveState");

            // Initialize EffectiveState with CurrentState for existing records
            migrationBuilder.Sql(@"
                UPDATE ""Instances""
                SET ""EffectiveState"" = ""CurrentState""
                WHERE ""CurrentState"" IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Instances_EffectiveState",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "SubFlowCurrentState",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "EffectiveState",
                table: "Instances");
        }
    }
}
