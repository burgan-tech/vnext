using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds the schema-local atomic compare-and-swap boundary used to append prepared
    /// <c>InstancesData</c> batches.
    /// </summary>
    public partial class AddInstanceDataConditionalBatchAppend : Migration
    {
        internal const string UpSql = """
CREATE OR REPLACE FUNCTION try_append_instance_data_batch(
    p_instance_id uuid,
    p_expected_data_id uuid,
    p_expected_etag text,
    p_rows jsonb)
RETURNS TABLE (
    "Status" text,
    "Id" uuid,
    "Version" text,
    "VersionNo" bigint,
    "HistorySequence" integer,
    "ETag" text,
    "DataHash" text,
    "Data" jsonb,
    "EnteredAt" timestamp with time zone,
    "IsLatest" boolean)
LANGUAGE plpgsql
SET search_path FROM CURRENT
AS $function$
DECLARE
    v_schema text := current_schema();
    v_latest_id uuid;
    v_latest_etag text;
    v_input_count integer;
    v_distinct_input_count integer;
    v_existing_count integer;
    v_expected_version_no bigint;
    v_max_input_version_no bigint;
    v_persisted_range_count integer;
    v_has_mismatch boolean;
    v_has_current_latest_in_batch boolean;
BEGIN
    IF jsonb_typeof(p_rows) IS DISTINCT FROM 'array' THEN
        RAISE EXCEPTION 'instance_data_batch_must_be_array' USING ERRCODE = 'P0001';
    END IF;

    v_input_count := jsonb_array_length(p_rows);
    IF v_input_count = 0 THEN
        RAISE EXCEPTION 'instance_data_batch_must_not_be_empty' USING ERRCODE = 'P0001';
    END IF;

    SELECT count(DISTINCT (element->>'DataId')::uuid)
      INTO v_distinct_input_count
      FROM jsonb_array_elements(p_rows) AS element;

    IF v_distinct_input_count <> v_input_count THEN
        RAISE EXCEPTION 'instance_data_duplicate_batch_id' USING ERRCODE = 'P0001';
    END IF;

    -- This is the same transaction-scoped lock key used by the insert trigger. It serializes
    -- head observation, idempotency validation, CAS, and the complete insert statement.
    PERFORM pg_advisory_xact_lock(hashtext(p_instance_id::text));

    EXECUTE format(
        'SELECT "Id", "ETag"::text FROM %I."InstancesData" '
        'WHERE "InstanceId" = $1 AND "IsLatest" = TRUE',
        v_schema)
    INTO v_latest_id, v_latest_etag
    USING p_instance_id;

    -- Count IDs in the complete schema, not only this instance. An existing ID attached to a
    -- different instance is an identity mismatch and must become a stable P0001 error below.
    EXECUTE format($sql$
        WITH input AS (
            SELECT (element->>'DataId')::uuid AS data_id
            FROM jsonb_array_elements($1) AS element
        )
        SELECT count(d."Id")
        FROM input i
        JOIN %I."InstancesData" d ON d."Id" = i.data_id
        $sql$, v_schema)
    INTO v_existing_count
    USING p_rows;

    IF v_existing_count = v_input_count THEN
        -- A complete replay is a no-op only when every persisted prepared field still matches.
        -- VersionNo is deliberately excluded because the trigger owns that value.
        EXECUTE format($sql$
            WITH input AS (
                SELECT
                    (element->>'DataId')::uuid AS data_id,
                    element->>'Version' AS version,
                    (element->>'HistorySequence')::integer AS history_sequence,
                    element->>'ETag' AS etag,
                    element->>'DataHash' AS data_hash,
                    element->'Data' AS data,
                    (element->>'EnteredAt')::timestamp with time zone AS entered_at,
                    (element->>'IsLatest')::boolean AS is_latest
                FROM jsonb_array_elements($1) AS element
            )
            SELECT EXISTS (
                SELECT 1
                FROM input i
                JOIN %I."InstancesData" d ON d."Id" = i.data_id
                WHERE d."InstanceId" IS DISTINCT FROM $2
                   OR d."Version" IS DISTINCT FROM i.version
                   OR d."HistorySequence" IS DISTINCT FROM i.history_sequence
                   OR d."ETag" IS DISTINCT FROM i.etag
                   OR d."DataHash" IS DISTINCT FROM i.data_hash
                   OR d."Data" IS DISTINCT FROM i.data
                   OR d."EnteredAt" IS DISTINCT FROM i.entered_at
                   OR d."IsLatest" IS DISTINCT FROM i.is_latest
            )
            $sql$, v_schema)
        INTO v_has_mismatch
        USING p_rows, p_instance_id;

        IF v_has_mismatch THEN
            RAISE EXCEPTION 'instance_data_idempotency_violation' USING ERRCODE = 'P0001';
        END IF;

        -- Validate that the supplied IDs form the complete persisted segment after the expected
        -- head. This rejects suffix/middle partial replays. If the batch advanced the latest
        -- pointer, it must also contain the currently latest row, rejecting a prefix replay.
        IF p_expected_data_id IS NULL THEN
            v_expected_version_no := 0;
        ELSE
            EXECUTE format(
                'SELECT "VersionNo" FROM %I."InstancesData" '
                'WHERE "InstanceId" = $1 AND "Id" = $2',
                v_schema)
            INTO v_expected_version_no
            USING p_instance_id, p_expected_data_id;

            IF v_expected_version_no IS NULL THEN
                RAISE EXCEPTION 'instance_data_idempotency_violation' USING ERRCODE = 'P0001';
            END IF;
        END IF;

        EXECUTE format($sql$
            WITH input AS (
                SELECT (element->>'DataId')::uuid AS data_id
                FROM jsonb_array_elements($1) AS element
            )
            SELECT max(d."VersionNo")
            FROM input i
            JOIN %I."InstancesData" d ON d."Id" = i.data_id
            $sql$, v_schema)
        INTO v_max_input_version_no
        USING p_rows;

        EXECUTE format(
            'SELECT count(*) FROM %I."InstancesData" '
            'WHERE "InstanceId" = $1 AND "VersionNo" > $2 AND "VersionNo" <= $3',
            v_schema)
        INTO v_persisted_range_count
        USING p_instance_id, v_expected_version_no, v_max_input_version_no;

        IF v_persisted_range_count <> v_input_count THEN
            RAISE EXCEPTION 'instance_data_partial_idempotency_violation' USING ERRCODE = 'P0001';
        END IF;

        IF v_latest_id IS DISTINCT FROM p_expected_data_id THEN
            SELECT EXISTS (
                SELECT 1
                FROM jsonb_array_elements(p_rows) AS element
                WHERE (element->>'DataId')::uuid = v_latest_id)
            INTO v_has_current_latest_in_batch;

            IF NOT v_has_current_latest_in_batch THEN
                RAISE EXCEPTION 'instance_data_partial_idempotency_violation' USING ERRCODE = 'P0001';
            END IF;
        END IF;

        RETURN QUERY EXECUTE format($sql$
            WITH input AS (
                SELECT
                    (element->>'DataId')::uuid AS data_id,
                    ordinality
                FROM jsonb_array_elements($1) WITH ORDINALITY AS item(element, ordinality)
            )
            SELECT
                'no_change'::text,
                d."Id",
                d."Version"::text,
                d."VersionNo",
                d."HistorySequence",
                d."ETag"::text,
                d."DataHash"::text,
                d."Data",
                d."EnteredAt",
                d."IsLatest"
            FROM input i
            JOIN %I."InstancesData" d ON d."Id" = i.data_id
            WHERE d."InstanceId" = $2
            ORDER BY i.ordinality
            $sql$, v_schema)
        USING p_rows, p_instance_id;
        RETURN;
    ELSIF v_existing_count > 0 THEN
        RAISE EXCEPTION 'instance_data_partial_idempotency_violation' USING ERRCODE = 'P0001';
    END IF;

    -- NULL expected ID/ETag is an exact CAS value: it matches only a data-less instance.
    IF v_latest_id IS DISTINCT FROM p_expected_data_id
       OR v_latest_etag IS DISTINCT FROM p_expected_etag THEN
        RETURN QUERY EXECUTE format($sql$
            SELECT
                'conflict'::text,
                d."Id",
                d."Version"::text,
                d."VersionNo",
                d."HistorySequence",
                d."ETag"::text,
                d."DataHash"::text,
                d."Data",
                d."EnteredAt",
                d."IsLatest"
            FROM (VALUES (1)) AS singleton(value)
            LEFT JOIN LATERAL (
                SELECT *
                FROM %I."InstancesData"
                WHERE "InstanceId" = $1 AND "IsLatest" = TRUE
                LIMIT 1
            ) AS d ON TRUE
            $sql$, v_schema)
        USING p_instance_id;
        RETURN;
    END IF;

    -- The whole ordered batch is inserted by one SQL statement. The data-modifying CTE then
    -- joins the returned persisted values back to ordinality, so output ordering never depends
    -- on PostgreSQL RETURNING order.
    RETURN QUERY EXECUTE format($sql$
        WITH input AS MATERIALIZED (
            SELECT
                (element->>'DataId')::uuid AS data_id,
                element->>'Version' AS version,
                (element->>'HistorySequence')::integer AS history_sequence,
                element->>'ETag' AS etag,
                element->>'DataHash' AS data_hash,
                element->'Data' AS data,
                (element->>'EnteredAt')::timestamp with time zone AS entered_at,
                (element->>'IsLatest')::boolean AS is_latest,
                ordinality
            FROM jsonb_array_elements($2) WITH ORDINALITY AS item(element, ordinality)
        ),
        inserted AS (
            INSERT INTO %I."InstancesData"
                ("Id", "InstanceId", "Version", "HistorySequence", "ETag",
                 "DataHash", "Data", "EnteredAt", "IsLatest")
            SELECT
                i.data_id,
                $1,
                i.version,
                i.history_sequence,
                i.etag,
                i.data_hash,
                i.data,
                i.entered_at,
                i.is_latest
            FROM input i
            ORDER BY i.ordinality
            RETURNING
                "Id",
                "Version",
                "VersionNo",
                "HistorySequence",
                "ETag",
                "DataHash",
                "Data",
                "EnteredAt",
                "IsLatest"
        )
        SELECT
            'applied'::text,
            inserted."Id",
            inserted."Version"::text,
            inserted."VersionNo",
            inserted."HistorySequence",
            inserted."ETag"::text,
            inserted."DataHash"::text,
            inserted."Data",
            inserted."EnteredAt",
            inserted."IsLatest"
        FROM input
        JOIN inserted ON inserted."Id" = input.data_id
        ORDER BY input.ordinality
        $sql$, v_schema)
    USING p_instance_id, p_rows;
END;
$function$;
""";

        internal const string DownSql =
            "DROP FUNCTION IF EXISTS try_append_instance_data_batch(uuid, uuid, text, jsonb);";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownSql);
        }
    }
}
