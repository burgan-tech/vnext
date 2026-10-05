using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Promotes the task's hook (<c>TaskTrigger</c>) and <c>Order</c> out of the <c>ExecutionKey</c>
    /// SHA-256 into first-class nullable columns on <c>InstanceTasks</c>, so the <c>tasks</c> system
    /// function can report which hook a journal row ran under — a state's OnEntry vs the triggering
    /// transition's OnExecute were previously indistinguishable (vnext-client-sdk-core#60).
    ///
    /// <para>
    /// Both columns are nullable and left null for rows written before this migration: the hook/order
    /// cannot be recovered from the one-way hash, and the API reports them as unknown rather than
    /// fabricating a value. New journal rows always populate them. No data migration; purely additive,
    /// so a runtime from before this change simply ignores the columns.
    /// </para>
    /// </summary>
    public partial class AddInstanceTaskTriggerAndOrderColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Order",
                schema: "public",
                table: "InstanceTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TaskTrigger",
                schema: "public",
                table: "InstanceTasks",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Order",
                schema: "public",
                table: "InstanceTasks");

            migrationBuilder.DropColumn(
                name: "TaskTrigger",
                schema: "public",
                table: "InstanceTasks");
        }
    }
}
