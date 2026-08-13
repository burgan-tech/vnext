using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Re-scopes <c>InstancesData.VersionNo</c> from an instance-global sequence to a
    /// line-scoped ordinal: a 1-based sequence WITHIN one semantic <c>Version</c> string
    /// (each new version line restarts at 1; same-version appends continue their line).
    /// Existing rows are renumbered per (InstanceId, Version) preserving their previous
    /// relative order, and the unique backstop moves from (InstanceId, VersionNo) to
    /// (InstanceId, Version, VersionNo). Down renumbers back to a per-instance global
    /// sequence (EnteredAt order, best effort) and restores the old index.
    /// </summary>
    public partial class LineScopeInstanceDataVersionNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InstancesData_Instance_VersionNo",
                schema: "public",
                table: "InstancesData");

            // Renumber existing rows per version line, keeping the old global VersionNo as
            // the intra-line order. MultiSchemaNpgsqlMigrationsSqlGenerator prepends
            // SET search_path per flow schema, so the unqualified name resolves per tenant.
            migrationBuilder.Sql(@"
UPDATE ""InstancesData"" d
SET ""VersionNo"" = t.rn
FROM (
    SELECT ""Id"",
           ROW_NUMBER() OVER (PARTITION BY ""InstanceId"", ""Version"" ORDER BY ""VersionNo"", ""EnteredAt"", ""Id"") AS rn
    FROM ""InstancesData""
) t
WHERE t.""Id"" = d.""Id"" AND d.""VersionNo"" <> t.rn;
            ");

            migrationBuilder.CreateIndex(
                name: "UX_InstancesData_Instance_Version_VersionNo",
                schema: "public",
                table: "InstancesData",
                columns: new[] { "InstanceId", "Version", "VersionNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InstancesData_Instance_Version_VersionNo",
                schema: "public",
                table: "InstancesData");

            // Best-effort return to a per-instance global sequence: chronological order.
            migrationBuilder.Sql(@"
UPDATE ""InstancesData"" d
SET ""VersionNo"" = t.rn
FROM (
    SELECT ""Id"",
           ROW_NUMBER() OVER (PARTITION BY ""InstanceId"" ORDER BY ""EnteredAt"", ""Id"") AS rn
    FROM ""InstancesData""
) t
WHERE t.""Id"" = d.""Id"" AND d.""VersionNo"" <> t.rn;
            ");

            migrationBuilder.CreateIndex(
                name: "UX_InstancesData_Instance_VersionNo",
                schema: "public",
                table: "InstancesData",
                columns: new[] { "InstanceId", "VersionNo" },
                unique: true);
        }
    }
}
