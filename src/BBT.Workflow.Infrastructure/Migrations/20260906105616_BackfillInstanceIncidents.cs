using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Data migration: copies the incidents embedded in the legacy <c>"Instances"."Incidents"</c> jsonb
    /// array into the <c>InstanceIncidents</c> table created by <see cref="MoveInstanceIncidentsToTable"/>
    /// and raises the denormalized <c>"Instances"."HasActiveIncident"</c> flag for every instance that
    /// still carries an unresolved one.
    /// </summary>
    /// <remarks>
    /// Runs once per schema (per-schema migration history) inside the migration transaction. Written
    /// to be idempotent anyway (<c>ON CONFLICT DO NOTHING</c>, flag only raised where still false) so a
    /// partially applied run can be replayed. Table names are unqualified because
    /// <c>MultiSchemaNpgsqlMigrationsSqlGenerator</c> prepends <c>SET search_path</c> for the target
    /// schema. JSON keys are the camelCase names the domain serialized with. The legacy column is left
    /// in place for this release; a later release drops it.
    /// </remarks>
    public partial class BackfillInstanceIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                                 INSERT INTO "InstanceIncidents"
                                     ("Id", "InstanceId", "CreatedAt", "State", "Transition", "Task", "Message",
                                      "StackTrace", "TraceId", "ErrorCode", "ErrorLayer", "StatusCode",
                                      "BoundaryAction", "BoundaryLevel", "IsResolved", "ResolvedAt", "RetryCount")
                                 SELECT
                                     COALESCE(NULLIF(e.elem->>'id', '')::uuid, gen_random_uuid()),
                                     i."Id",
                                     COALESCE(NULLIF(e.elem->>'createdAt', '')::timestamptz, i."CreatedAt"),
                                     LEFT(COALESCE(e.elem->>'state', ''), 100),
                                     LEFT(COALESCE(e.elem->>'transition', ''), 100),
                                     LEFT(e.elem->>'task', 100),
                                     LEFT(COALESCE(e.elem->>'message', ''), 1024),
                                     LEFT(e.elem->>'stackTrace', 4096),
                                     LEFT(e.elem->>'traceId', 64),
                                     LEFT(COALESCE(e.elem->>'errorCode', 'Unknown'), 256),
                                     LEFT(COALESCE(e.elem->>'errorLayer', 'Pipeline'), 64),
                                     NULLIF(e.elem->>'statusCode', '')::int,
                                     LEFT(e.elem->>'boundaryAction', 64),
                                     LEFT(e.elem->>'boundaryLevel', 64),
                                     COALESCE(NULLIF(e.elem->>'isResolved', '')::boolean, false),
                                     NULLIF(e.elem->>'resolvedAt', '')::timestamptz,
                                     COALESCE(NULLIF(e.elem->>'retryCount', '')::int, 0)
                                 FROM "Instances" i
                                 CROSS JOIN LATERAL jsonb_array_elements(i."Incidents") AS e(elem)
                                 WHERE i."Incidents" IS NOT NULL
                                   AND jsonb_typeof(i."Incidents") = 'array'
                                 ON CONFLICT ("Id") DO NOTHING;
                                 """);

            migrationBuilder.Sql("""
                                 UPDATE "Instances" i
                                 SET "HasActiveIncident" = true
                                 FROM (SELECT DISTINCT "InstanceId" FROM "InstanceIncidents" WHERE NOT "IsResolved") a
                                 WHERE i."Id" = a."InstanceId"
                                   AND NOT i."HasActiveIncident";
                                 """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally a no-op: the legacy jsonb column is untouched by Up, and reverting
            // MoveInstanceIncidentsToTable drops the InstanceIncidents table (and with it the copied rows).
        }
    }
}
