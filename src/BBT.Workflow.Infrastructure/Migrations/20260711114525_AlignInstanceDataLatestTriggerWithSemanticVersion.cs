using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Aligns the InstanceData versioning trigger with the domain's semantic-version
    /// <c>IsLatest</c> invariant. Previously the trigger unconditionally demoted the current
    /// latest and set <c>NEW."IsLatest" := TRUE</c> on every insert (last-insert-wins), which
    /// overrode the application layer: appending a lower semantic version (e.g. 1.0.5 while
    /// 2.0.0 is the head) stole the latest flag at the DB level even though the domain
    /// (<c>Instance.AddDataWithVersion</c>) correctly created the row with <c>IsLatest = false</c>.
    /// <para>
    /// The trigger now RESPECTS the application's decision: it still assigns the monotonic
    /// <c>VersionNo</c> under the per-instance advisory lock, but only demotes the previous
    /// latest when the incoming row is itself marked latest. The semantic-version comparison
    /// that decides "latest" stays in the domain (single source of truth); the trigger performs
    /// the demotion atomically inside the BEFORE INSERT so the partial unique index
    /// <c>UX_InstancesData_Instance_IsLatest</c> is never transiently violated regardless of EF
    /// statement ordering.
    /// </para>
    /// </summary>
    public partial class AlignInstanceDataLatestTriggerWithSemanticVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

    -- IsLatest ownership belongs to the application layer, which decides via the semantic
    -- version comparer (Instance.AddData / AddDataWithVersion). Only when the incoming row is
    -- marked latest do we demote the previous latest -- atomically, inside this BEFORE INSERT,
    -- so the partial unique index is never transiently violated. When the application inserts
    -- an older-line row (NEW.IsLatest = FALSE), the current latest is left untouched.
    IF NEW.""IsLatest"" THEN
        EXECUTE format(
            'UPDATE %I.""InstancesData"" SET ""IsLatest"" = FALSE WHERE ""InstanceId"" = $1 AND ""IsLatest"" = TRUE',
            TG_TABLE_SCHEMA
        ) USING NEW.""InstanceId"";
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert to the previous unconditional last-insert-wins behavior.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION set_instance_data_version_and_latest()
RETURNS trigger AS $$
DECLARE
    next_version_no bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext(NEW.""InstanceId""::text));

    EXECUTE format(
        'SELECT COALESCE(MAX(""VersionNo""), 0) + 1 FROM %I.""InstancesData"" WHERE ""InstanceId"" = $1',
        TG_TABLE_SCHEMA
    ) INTO next_version_no USING NEW.""InstanceId"";

    NEW.""VersionNo"" := next_version_no;

    EXECUTE format(
        'UPDATE %I.""InstancesData"" SET ""IsLatest"" = FALSE WHERE ""InstanceId"" = $1 AND ""IsLatest"" = TRUE',
        TG_TABLE_SCHEMA
    ) USING NEW.""InstanceId"";

    NEW.""IsLatest"" := TRUE;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
            ");
        }
    }
}
