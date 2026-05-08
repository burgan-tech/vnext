using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddInstance_StateTypesAndStage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrentStateSubType",
                schema: "public",
                table: "Instances",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentStateType",
                schema: "public",
                table: "Instances",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                schema: "public",
                table: "Instances",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentStateSubType",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "CurrentStateType",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "Stage",
                schema: "public",
                table: "Instances");
        }
    }
}
