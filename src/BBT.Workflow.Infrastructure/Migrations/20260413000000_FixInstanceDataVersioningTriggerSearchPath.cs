using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Fixes the <c>set_instance_data_version_and_latest</c> trigger function so that it
    /// resolves table names relative to the schema it lives in, not the connection's
    /// <c>search_path</c> (which is no longer set under PgBouncer-safe multi-schema mode).
    ///
    /// The original function was created without an explicit <c>search_path</c> setting,
    /// so its internal SQL (<c>SELECT … FROM "InstancesData"</c>) relied on the session-level
    /// <c>search_path</c>.  Now that we never issue <c>SET search_path</c>, the function fails
    /// with <c>42P01: relation "InstancesData" does not exist</c> when called from any schema
    /// other than <c>public</c>.
    ///
    /// The fix uses <c>SET LOCAL search_path TO pg_catalog, pg_temp</c> inside the function
    /// body and qualifies every table reference with <c>current_schema()</c>.  PostgreSQL
    /// creates the function in the schema that owns the migration (determined by the
    /// <c>search_path</c> injected by <see cref="MultiSchemaNpgsqlMigrationsSqlGenerator"/>
    /// before each <c>SqlOperation</c>), so <c>current_schema()</c> always returns the correct
    /// tenant schema at the time the function body executes.
    /// </summary>
    public partial class FixInstanceDataVersioningTriggerSearchPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Re-create the trigger function with schema-safe table references.
            // TG_TABLE_SCHEMA is a built-in trigger variable that always contains the schema
            // of the table that fired the trigger — exactly the tenant schema we need.
            // EXECUTE format(..., TG_TABLE_SCHEMA) qualifies every table reference explicitly,
            // so no search_path is required and the function works correctly under PgBouncer
            // transaction-mode pooling where search_path is never set.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION set_instance_data_version_and_latest()
RETURNS trigger AS $$
DECLARE
    next_version_no bigint;
BEGIN
    -- Instance-level advisory lock (transaction-scoped, auto-released on commit/rollback).
    PERFORM pg_advisory_xact_lock(hashtext(NEW.""InstanceId""::text));

    -- Get next version number for this instance.
    -- TG_TABLE_SCHEMA is the schema of the table that fired the trigger (the tenant schema).
    EXECUTE format(
        'SELECT COALESCE(MAX(""VersionNo""), 0) + 1 FROM %I.""InstancesData"" WHERE ""InstanceId"" = $1',
        TG_TABLE_SCHEMA
    ) INTO next_version_no USING NEW.""InstanceId"";

    NEW.""VersionNo"" := next_version_no;

    -- Mark previous latest record as not latest.
    EXECUTE format(
        'UPDATE %I.""InstancesData"" SET ""IsLatest"" = FALSE WHERE ""InstanceId"" = $1 AND ""IsLatest"" = TRUE',
        TG_TABLE_SCHEMA
    ) USING NEW.""InstanceId"";

    -- Mark this record as the latest.
    NEW.""IsLatest"" := TRUE;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert to the original unqualified version.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION set_instance_data_version_and_latest()
RETURNS trigger AS $$
DECLARE
    next_version_no bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext(NEW.""InstanceId""::text));

    SELECT COALESCE(MAX(""VersionNo""), 0) + 1
      INTO next_version_no
      FROM ""InstancesData""
     WHERE ""InstanceId"" = NEW.""InstanceId"";

    NEW.""VersionNo"" := next_version_no;

    UPDATE ""InstancesData""
       SET ""IsLatest"" = FALSE
     WHERE ""InstanceId"" = NEW.""InstanceId""
       AND ""IsLatest"" = TRUE;

    NEW.""IsLatest"" := TRUE;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
            ");
        }
    }
}
