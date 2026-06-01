using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditedInstanceCorrelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                schema: "public",
                table: "InstancesCorrelations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                schema: "public",
                table: "InstancesCorrelations",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByBehalfOf",
                schema: "public",
                table: "InstancesCorrelations",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                schema: "public",
                table: "InstancesCorrelations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                schema: "public",
                table: "InstancesCorrelations",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModifiedByBehalfOf",
                schema: "public",
                table: "InstancesCorrelations",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "public",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                schema: "public",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "CreatedByBehalfOf",
                schema: "public",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                schema: "public",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                schema: "public",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "ModifiedByBehalfOf",
                schema: "public",
                table: "InstancesCorrelations");
        }
    }
}
