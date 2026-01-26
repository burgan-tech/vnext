using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceStateTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EffectiveStateSubType",
                table: "Instances",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EffectiveStateType",
                table: "Instances",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveStateSubType",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "EffectiveStateType",
                table: "Instances");
        }
    }
}
