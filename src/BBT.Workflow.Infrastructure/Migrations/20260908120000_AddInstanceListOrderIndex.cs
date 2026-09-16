using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BBT.Workflow.Migrations;

/// <summary>Adds the general instance-list order index during schema maintenance.</summary>
[DbContext(typeof(WorkflowDbContext))]
[Migration("20260908120000_AddInstanceListOrderIndex")]
public sealed class AddInstanceListOrderIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The multi-schema SQL generator sets search_path for the flow. Reuse an equivalent
        // existing index, including installations that previously installed it for benchmarking.
        migrationBuilder.Sql("""
            DO $migration$
            BEGIN
              IF NOT EXISTS (
                SELECT 1 FROM pg_index i
                JOIN pg_class ix ON ix.oid = i.indexrelid
                JOIN pg_am am ON am.oid = ix.relam
                JOIN pg_attribute created ON created.attrelid = i.indrelid AND created.attname = 'CreatedAt'
                JOIN pg_attribute id ON id.attrelid = i.indrelid AND id.attname = 'Id'
                WHERE i.indrelid = '"Instances"'::regclass
                  AND i.indisvalid AND i.indisready AND i.indpred IS NULL AND i.indexprs IS NULL
                  AND am.amname = 'btree' AND i.indnkeyatts = 2
                  AND i.indkey[0] = created.attnum AND i.indkey[1] = id.attnum
                  AND i.indoption[0] = 3 AND i.indoption[1] = 0
              ) THEN
                CREATE INDEX "IX_Instances_CreatedAt_Id" ON "Instances" ("CreatedAt" DESC, "Id" ASC);
                COMMENT ON INDEX "IX_Instances_CreatedAt_Id" IS 'vnext:issue-933:list-order:v1';
              END IF;
            END $migration$;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $migration$
            BEGIN
              IF obj_description(to_regclass('"IX_Instances_CreatedAt_Id"'), 'pg_class') = 'vnext:issue-933:list-order:v1' THEN
                DROP INDEX "IX_Instances_CreatedAt_Id";
              END IF;
            END $migration$;
            """);
    }
}
