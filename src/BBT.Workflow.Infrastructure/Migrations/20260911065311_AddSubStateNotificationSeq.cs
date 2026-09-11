using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds the ordering watermark for <c>sub:state-changed</c> notifications:
    /// <c>"Instances"."SubStateNotificationSeq"</c> (the publisher's counter, incremented inside the
    /// transaction that publishes) and <c>"InstancesCorrelations"."SubFlowNotificationSeq"</c> (the
    /// highest number the receiver has applied for that sub-item).
    /// </summary>
    /// <remarks>
    /// Replaces a wall-clock comparison that cannot order two notifications produced on different
    /// pods. Both default to 0, which the receiver reads as "not reported" and falls back to the
    /// timestamp guard for — so events already in flight during the rollout keep working.
    /// </remarks>
    public partial class AddSubStateNotificationSeq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SubFlowNotificationSeq",
                schema: "public",
                table: "InstancesCorrelations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SubStateNotificationSeq",
                schema: "public",
                table: "Instances",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubFlowNotificationSeq",
                schema: "public",
                table: "InstancesCorrelations");

            migrationBuilder.DropColumn(
                name: "SubStateNotificationSeq",
                schema: "public",
                table: "Instances");
        }
    }
}
