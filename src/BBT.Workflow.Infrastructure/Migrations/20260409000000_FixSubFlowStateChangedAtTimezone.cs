using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <inheritdoc />
    public partial class FixSubFlowStateChangedAtTimezone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE ""InstancesCorrelations"" ALTER COLUMN ""SubFlowStateChangedAt"" TYPE timestamp with time zone USING ""SubFlowStateChangedAt"" AT TIME ZONE 'UTC'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE ""InstancesCorrelations"" ALTER COLUMN ""SubFlowStateChangedAt"" TYPE timestamp without time zone USING ""SubFlowStateChangedAt"" AT TIME ZONE 'UTC'");
        }
    }
}
