using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceTransitionEffectiveState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EffectiveState",
                schema: "public",
                table: "InstanceTransitions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EffectiveStateSubType",
                schema: "public",
                table: "InstanceTransitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EffectiveStateType",
                schema: "public",
                table: "InstanceTransitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                schema: "public",
                table: "InstanceTransitions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveState",
                schema: "public",
                table: "InstanceTransitions");

            migrationBuilder.DropColumn(
                name: "EffectiveStateSubType",
                schema: "public",
                table: "InstanceTransitions");

            migrationBuilder.DropColumn(
                name: "EffectiveStateType",
                schema: "public",
                table: "InstanceTransitions");

            migrationBuilder.DropColumn(
                name: "Stage",
                schema: "public",
                table: "InstanceTransitions");
        }
    }
}
