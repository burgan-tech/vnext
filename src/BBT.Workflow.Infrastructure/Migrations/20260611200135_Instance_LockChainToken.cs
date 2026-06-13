using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class Instance_LockChainToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ChainHeartbeatAt",
                schema: "public",
                table: "Instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ChainToken",
                schema: "public",
                table: "Instances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResumePointStepOrder",
                schema: "public",
                table: "Instances",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_ChainHeartbeatAt",
                schema: "public",
                table: "Instances",
                column: "ChainHeartbeatAt",
                filter: "\"ChainHeartbeatAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_ChainToken",
                schema: "public",
                table: "Instances",
                column: "ChainToken",
                filter: "\"ChainToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Instances_ChainHeartbeatAt",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropIndex(
                name: "IX_Instances_ChainToken",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ChainHeartbeatAt",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ChainToken",
                schema: "public",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ResumePointStepOrder",
                schema: "public",
                table: "Instances");
        }
    }
}
