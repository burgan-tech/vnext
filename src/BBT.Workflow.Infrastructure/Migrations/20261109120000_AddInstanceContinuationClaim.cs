using System;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds <c>Instances.ContinuationClaimedAt</c>, the updateData continuation claim on a parked
    /// Busy instance. Two updateData executions that both satisfy the auto gate of the state the
    /// instance is parked in used to take the parked Busy over together and run the same auto chain
    /// in parallel (contract domain: <c>all-documents-ready</c> two to three times per contract).
    /// The claim is a set-based compare-and-set guarded by status, current state and the claim's
    /// age; exactly one execution wins and the other drops its continuation.
    ///
    /// <para>
    /// Nullable, no default, no index (the claim is only ever read by primary key in the same
    /// UPDATE that writes it). Purely additive: a runtime from before this change ignores it.
    /// </para>
    /// </summary>
    [DbContext(typeof(WorkflowDbContext))]
    [Migration("20261109120000_AddInstanceContinuationClaim")]
    public partial class AddInstanceContinuationClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ContinuationClaimedAt",
                schema: "public",
                table: "Instances",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContinuationClaimedAt",
                schema: "public",
                table: "Instances");
        }
    }
}
