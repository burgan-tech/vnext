using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations.MessagingDb
{
    /// <inheritdoc />
    public partial class AddPartitionIdAndPartialDispatchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the new discriminator column first. A smallint NOT NULL DEFAULT 0 is a
            // metadata-only change on PostgreSQL 11+ (no table rewrite, no long lock).
            migrationBuilder.AddColumn<short>(
                name: "PartitionId",
                schema: "sys_queues",
                table: "OutboxMessages",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "PartitionId",
                schema: "sys_queues",
                table: "InboxMessages",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            // Build every new index BEFORE dropping any old one, so a dispatch/retention query
            // always has a usable index to hit. CONCURRENTLY avoids the ACCESS EXCLUSIVE lock a
            // plain CREATE INDEX would take, which would otherwise block writes on these hot,
            // continuously-polled tables. CONCURRENTLY cannot run inside a transaction, hence
            // suppressTransaction: true on every statement in this migration.
            //
            // All four new names (IX_*_Dispatch, IX_*_Retention) are DISTINCT from the old
            // names they replace (IX_*_Processing, IX_*_Cleanup) -- the retention index used to
            // reuse the "_Cleanup" name while changing shape (2-column non-partial -> 1-column
            // partial), which forced a build-under-temp-name/rename dance to avoid "IF NOT
            // EXISTS" silently no-op'ing onto the stale definition. Renaming it to "_Retention"
            // in the Aether model (see BBT.Aether.Infrastructure OutboxModelBuilderExtensions /
            // InboxModelBuilderExtensions) removes the collision entirely: every statement below
            // is a plain, independent create against a name nothing else currently holds.
            //
            // Each CREATE is preceded by an unconditional DROP INDEX CONCURRENTLY IF EXISTS for
            // the SAME (new) name. On a first run this is a no-op (the name doesn't exist yet).
            // On a retry after a prior failed/partial run, it clears out any leftover NOT VALID
            // index from the earlier attempt, so "IF NOT EXISTS" on the following CREATE cannot
            // skip a rebuild that is actually needed -- without this, a once-failed index would
            // wedge every subsequent retry identically, since CREATE INDEX CONCURRENTLY IF NOT
            // EXISTS treats "name exists" (even invalid) as "nothing to do". This is safe to do
            // unconditionally because none of these four names ever collide with a still-live
            // index: they are new names, so a retry can never drop something currently serving
            // queries.
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_OutboxMessages_Dispatch";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_OutboxMessages_Dispatch"
                ON sys_queues."OutboxMessages" ("PartitionId", "NextRetryAt", "CreatedAt")
                WHERE "Status" IN (0, 1);
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_InboxMessages_Dispatch";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_InboxMessages_Dispatch"
                ON sys_queues."InboxMessages" ("PartitionId", "NextRetryTime", "CreatedAt")
                WHERE "Status" IN (0, 1);
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_OutboxMessages_Retention";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_OutboxMessages_Retention"
                ON sys_queues."OutboxMessages" ("ProcessedAt")
                WHERE "Status" = 2;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_InboxMessages_Retention";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_InboxMessages_Retention"
                ON sys_queues."InboxMessages" ("HandledTime")
                WHERE "Status" = 2;
                """, suppressTransaction: true);

            // Guard: CREATE INDEX CONCURRENTLY can fail partway through (e.g. it is cancelled, or
            // a concurrent writer trips a deadlock) and leave the index behind marked NOT VALID,
            // rather than rolling back. Such an index is silently invisible to the query planner.
            // Because the next step drops the old (still-valid) indexes, an index left NOT VALID
            // here followed by a successful drop of the old ones would leave the table with NO
            // usable dispatch/retention index at all -- every lease and retention-cleanup query
            // would fall back to a sequential scan on a hot, high-traffic table.
            //
            // This checks BOTH existence and validity for all four new indexes -- counting rows
            // in pg_index matched by name and filtering on "NOT indisvalid" is not enough, since
            // that would be vacuously true (zero invalid rows) if an index doesn't exist at all.
            // Instead we match existing indexes by name (via indexrelid::regclass::text, which
            // cannot throw -- a missing index simply doesn't appear in pg_index) and require
            // exactly 4 matches that are also valid, so both "missing" and "present but invalid"
            // are caught and named in one custom, actionable error.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    valid_count integer;
                BEGIN
                    SELECT count(*) INTO valid_count
                    FROM pg_index i
                    WHERE i.indexrelid::regclass::text IN (
                        'sys_queues."IX_OutboxMessages_Dispatch"',
                        'sys_queues."IX_InboxMessages_Dispatch"',
                        'sys_queues."IX_OutboxMessages_Retention"',
                        'sys_queues."IX_InboxMessages_Retention"'
                    )
                    AND i.indisvalid;

                    IF valid_count <> 4 THEN
                        RAISE EXCEPTION
                            'AddPartitionIdAndPartialDispatchIndex: expected 4 valid indexes (IX_OutboxMessages_Dispatch, IX_InboxMessages_Dispatch, IX_OutboxMessages_Retention, IX_InboxMessages_Retention) but found % valid/present. Aborting before dropping the old _Processing/_Cleanup indexes so a usable index remains. The operator must DROP INDEX CONCURRENTLY the missing/invalid one(s) (a retry of this migration will then rebuild them) before re-running.',
                            valid_count;
                    END IF;
                END $$;
                """, suppressTransaction: true);

            // Only now drop the old, superseded indexes -- the new ones are confirmed present
            // and valid. These names never collide with the four new ones above, so this step
            // cannot remove something still serving a query.
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_OutboxMessages_Processing";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_InboxMessages_Processing";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_OutboxMessages_Cleanup";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_InboxMessages_Cleanup";
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirror image of Up: build everything the pre-migration schema needs before
            // touching what currently exists, validate, then drop the post-migration indexes.
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_OutboxMessages_Processing";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_OutboxMessages_Processing"
                ON sys_queues."OutboxMessages" ("Status", "LockedUntil", "NextRetryAt", "CreatedAt");
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_InboxMessages_Processing";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_InboxMessages_Processing"
                ON sys_queues."InboxMessages" ("Status", "LockedUntil", "NextRetryTime", "CreatedAt");
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_OutboxMessages_Cleanup";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_OutboxMessages_Cleanup"
                ON sys_queues."OutboxMessages" ("ProcessedAt", "CreatedAt");
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_InboxMessages_Cleanup";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_InboxMessages_Cleanup"
                ON sys_queues."InboxMessages" ("Status", "HandledTime");
                """, suppressTransaction: true);

            // Same existence+validity guard as Up, mirrored: abort before dropping the current
            // (post-migration) dispatch/retention indexes if any restored index is missing or
            // failed to build.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    valid_count integer;
                BEGIN
                    SELECT count(*) INTO valid_count
                    FROM pg_index i
                    WHERE i.indexrelid::regclass::text IN (
                        'sys_queues."IX_OutboxMessages_Processing"',
                        'sys_queues."IX_InboxMessages_Processing"',
                        'sys_queues."IX_OutboxMessages_Cleanup"',
                        'sys_queues."IX_InboxMessages_Cleanup"'
                    )
                    AND i.indisvalid;

                    IF valid_count <> 4 THEN
                        RAISE EXCEPTION
                            'AddPartitionIdAndPartialDispatchIndex (Down): expected 4 valid indexes (IX_OutboxMessages_Processing, IX_InboxMessages_Processing, IX_OutboxMessages_Cleanup, IX_InboxMessages_Cleanup) but found % valid/present. Aborting before dropping the dispatch/retention indexes so a usable index remains. The operator must DROP INDEX CONCURRENTLY the missing/invalid one(s) (a retry of this rollback will then rebuild them) before re-running.',
                            valid_count;
                    END IF;
                END $$;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_OutboxMessages_Dispatch";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_InboxMessages_Dispatch";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_OutboxMessages_Retention";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS sys_queues."IX_InboxMessages_Retention";
                """, suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "PartitionId",
                schema: "sys_queues",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "PartitionId",
                schema: "sys_queues",
                table: "InboxMessages");
        }
    }
}
