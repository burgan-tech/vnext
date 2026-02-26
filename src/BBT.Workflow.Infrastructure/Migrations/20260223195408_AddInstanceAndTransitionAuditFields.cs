using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceAndTransitionAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "InstanceTransitions",
                type: "timestamp without time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "InstanceTransitions",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByBehalfOf",
                table: "InstanceTransitions",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TriggerType",
                table: "InstanceTransitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "Instances",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByBehalfOf",
                table: "Instances",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "Instances",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModifiedByBehalfOf",
                table: "Instances",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "InstanceTransitions");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "InstanceTransitions");

            migrationBuilder.DropColumn(
                name: "CreatedByBehalfOf",
                table: "InstanceTransitions");

            migrationBuilder.DropColumn(
                name: "TriggerType",
                table: "InstanceTransitions");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "CreatedByBehalfOf",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ModifiedByBehalfOf",
                table: "Instances");
        }
    }
}
