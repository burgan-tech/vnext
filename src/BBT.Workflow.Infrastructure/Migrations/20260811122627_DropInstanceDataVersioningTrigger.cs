using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Removes the InstanceData versioning trigger: VersionNo assignment, semantic-version
    /// rebase and stale-latest demotion now happen application-side in the InstanceData write
    /// funnel (<c>WorkflowDbContext.SaveChangesAsync</c>), under a per-instance
    /// <c>SELECT ... FOR UPDATE</c> row lock inside the writing transaction.
    /// <para>
    /// The unique indexes stay as the database-level backstop for both invariants:
    /// <c>UX_InstancesData_Instance_VersionNo</c> (no duplicate VersionNo per instance) and
    /// <c>UX_InstancesData_Instance_IsLatest</c> (at most one latest row per instance).
    /// Rolling deploys are safe in either order — the funnel assigns the same MAX+1 the
    /// trigger would (both serialized on the same per-instance lock), so trigger and funnel
    /// can coexist during the window.
    /// </para>
    /// </summary>
    public partial class DropInstanceDataVersioningTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MultiSchemaNpgsqlMigrationsSqlGenerator prepends SET search_path per flow schema,
            // so the unqualified names resolve to each tenant schema's objects.
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_instancesdata_set_version_and_latest ON ""InstancesData"";
DROP FUNCTION IF EXISTS set_instance_data_version_and_latest();
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the trigger exactly as 20260711114525 left it (advisory-lock MAX+1 with
            // application-owned IsLatest), so rolling back the funnel restores DB-side safety.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION set_instance_data_version_and_latest()
RETURNS trigger AS $$
DECLARE
    next_version_no bigint;
BEGIN
    -- Instance-level advisory lock (transaction-scoped, auto-released on commit/rollback).
    PERFORM pg_advisory_xact_lock(hashtext(NEW.""InstanceId""::text));

    -- Monotonic per-instance version number (insert order). TG_TABLE_SCHEMA is the tenant
    -- schema of the table that fired the trigger; explicit qualification keeps this correct
    -- under PgBouncer transaction-mode pooling where search_path is never set.
    EXECUTE format(
        'SELECT COALESCE(MAX(""VersionNo""), 0) + 1 FROM %I.""InstancesData"" WHERE ""InstanceId"" = $1',
        TG_TABLE_SCHEMA
    ) INTO next_version_no USING NEW.""InstanceId"";

    NEW.""VersionNo"" := next_version_no;

    IF NEW.""IsLatest"" THEN
        EXECUTE format(
            'UPDATE %I.""InstancesData"" SET ""IsLatest"" = FALSE WHERE ""InstanceId"" = $1 AND ""IsLatest"" = TRUE',
            TG_TABLE_SCHEMA
        ) USING NEW.""InstanceId"";
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_instancesdata_set_version_and_latest ON ""InstancesData"";
CREATE TRIGGER trg_instancesdata_set_version_and_latest
    BEFORE INSERT ON ""InstancesData""
    FOR EACH ROW
    EXECUTE FUNCTION set_instance_data_version_and_latest();
            ");
        }
    }
}
