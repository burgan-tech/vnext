using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Migrations;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

public sealed class InstanceDataConditionalAppendFunctionTests : IAsyncLifetime
{
    private const string TenantA = "tenant_a";
    private const string TenantB = "tenant_b";

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await CreateTenantSchemaAsync(TenantA);
        await CreateTenantSchemaAsync(TenantB);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Stale_expected_head_should_return_conflict_without_writing_and_include_observed_head()
    {
        var baseline = await SeedBaselineAsync(TenantA, """{"base":1}""");
        var remote = await InsertHeadAsync(TenantA, baseline.InstanceId, """{"remote":2}""");
        var local = Rows(("""{"local":3}""", true));

        var result = await CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            local);

        var conflict = result.ShouldHaveSingleItem();
        conflict.Status.ShouldBe("conflict");
        conflict.Id.ShouldBe(remote.Id);
        conflict.Version.ShouldBe(remote.Version);
        conflict.VersionNo.ShouldBe(remote.VersionNo);
        conflict.HistorySequence.ShouldBe(remote.HistorySequence);
        conflict.ETag.ShouldBe(remote.ETag);
        conflict.DataHash.ShouldBe(remote.DataHash);
        conflict.Data.ShouldBe(remote.Data);
        conflict.EnteredAt.ShouldBe(remote.EnteredAt);
        conflict.IsLatest.ShouldBe(true);

        (await ReadRowsAsync(TenantA, baseline.InstanceId)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Null_expected_head_should_apply_first_batch_to_data_less_instance()
    {
        var instanceId = Guid.NewGuid();
        var rows = Rows(("""{"first":true}""", true));

        var result = await CallFunctionAsync(TenantA, instanceId, null, null, rows);

        var applied = result.ShouldHaveSingleItem();
        applied.Status.ShouldBe("applied");
        applied.Id.ShouldBe(rows[0].DataId);
        applied.VersionNo.ShouldBe(1);
        applied.IsLatest.ShouldBe(true);

        var persisted = await ReadRowsAsync(TenantA, instanceId);
        persisted.ShouldHaveSingleItem().Id.ShouldBe(rows[0].DataId);
    }

    [Fact]
    public async Task Null_expected_head_should_conflict_when_latest_row_exists()
    {
        var baseline = await SeedBaselineAsync(TenantA, """{"base":1}""");

        var result = await CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            null,
            null,
            Rows(("""{"local":2}""", true)));

        var conflict = result.ShouldHaveSingleItem();
        conflict.Status.ShouldBe("conflict");
        conflict.Id.ShouldBe(baseline.DataId);
        (await ReadRowsAsync(TenantA, baseline.InstanceId)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Middle_row_failure_should_roll_back_complete_batch()
    {
        var baseline = await SeedBaselineAsync(TenantA, """{"base":1}""");
        var rows = new[]
        {
            Row("""{"a":1}""", false),
            Row("""{"b":2}""", false, new string('x', 181)),
            Row("""{"c":3}""", true)
        };

        var exception = await Should.ThrowAsync<PostgresException>(() => CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            rows));

        exception.SqlState.ShouldBe(PostgresErrorCodes.StringDataRightTruncation);
        var persisted = await ReadRowsAsync(TenantA, baseline.InstanceId);
        persisted.ShouldHaveSingleItem().IsLatest.ShouldBeTrue();
        persisted[0].Id.ShouldBe(baseline.DataId);
    }

    [Fact]
    public async Task Repeated_identical_complete_batch_should_return_no_change_in_input_order()
    {
        var baseline = await SeedBaselineAsync(TenantA, """{"base":1}""");
        var rows = Rows(("""{"a":1}""", false), ("""{"b":2}""", true));

        var applied = await CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            rows);
        var replay = await CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            rows);

        applied.Select(x => x.Status).ShouldAllBe(status => status == "applied");
        replay.Select(x => x.Status).ShouldAllBe(status => status == "no_change");
        applied.Select(x => x.Id).ShouldBe(rows.Select(x => (Guid?)x.DataId));
        replay.Select(x => x.Id).ShouldBe(rows.Select(x => (Guid?)x.DataId));
        (await ReadRowsAsync(TenantA, baseline.InstanceId)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Replay_with_only_some_ids_should_raise_explicit_partial_idempotency_error()
    {
        var baseline = await SeedBaselineAsync(TenantA, """{"base":1}""");
        var rows = Rows(("""{"a":1}""", false), ("""{"b":2}""", true));
        await CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            rows);

        var exception = await Should.ThrowAsync<PostgresException>(() => CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            rows.Take(1).ToArray()));

        exception.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        exception.MessageText.ShouldBe("instance_data_partial_idempotency_violation");
        (await ReadRowsAsync(TenantA, baseline.InstanceId)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Replay_with_same_id_and_different_field_should_raise_explicit_idempotency_error()
    {
        var baseline = await SeedBaselineAsync(TenantA, """{"base":1}""");
        var rows = Rows(("""{"changed":true}""", true));
        await CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            rows);
        var changedRows = new[] { rows[0] with { ETag = Ulid.NewUlid().ToString() } };

        var exception = await Should.ThrowAsync<PostgresException>(() => CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            changedRows));

        exception.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        exception.MessageText.ShouldBe("instance_data_idempotency_violation");
        (await ReadRowsAsync(TenantA, baseline.InstanceId)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Applied_batch_should_have_monotonic_version_numbers_and_exactly_one_latest_row()
    {
        var baseline = await SeedBaselineAsync(TenantA, """{"base":1}""");
        var rows = Rows(
            ("""{"a":1}""", false),
            ("""{"b":2}""", false),
            ("""{"c":3}""", true));

        var result = await CallFunctionAsync(
            TenantA,
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            rows);

        result.Select(x => x.VersionNo).ShouldBe(new long?[] { 2, 3, 4 });
        result.Select(x => x.Id).ShouldBe(rows.Select(x => (Guid?)x.DataId));

        var persisted = await ReadRowsAsync(TenantA, baseline.InstanceId);
        persisted.Select(x => x.VersionNo).ShouldBe(new long[] { 1, 2, 3, 4 });
        persisted.Count(x => x.IsLatest).ShouldBe(1);
        persisted.Single(x => x.IsLatest).Id.ShouldBe(rows[2].DataId);
    }

    [Fact]
    public async Task Same_instance_id_in_two_schemas_should_remain_isolated()
    {
        var instanceId = Guid.NewGuid();
        var a = await SeedBaselineAsync(TenantA, """{"tenant":"a"}""", instanceId);
        await SeedBaselineAsync(TenantB, """{"tenant":"b"}""", instanceId);

        await CallFunctionAsync(
            TenantA,
            instanceId,
            a.DataId,
            a.ETag,
            Rows(("""{"changed":true}""", true)));

        var tenantALatest = await ReadLatestAsync(TenantA, instanceId);
        var tenantBLatest = await ReadLatestAsync(TenantB, instanceId);
        tenantALatest.Data.ShouldBe("""{"changed": true}""");
        tenantBLatest.Data.ShouldBe("""{"tenant": "b"}""");
        (await ReadRowsAsync(TenantA, instanceId)).Count.ShouldBe(2);
        (await ReadRowsAsync(TenantB, instanceId)).ShouldHaveSingleItem();
    }

    private async Task CreateTenantSchemaAsync(string schema)
    {
        var quotedSchema = QuoteIdentifier(schema);
        var sql = $$"""
CREATE SCHEMA {{quotedSchema}};

CREATE TABLE {{quotedSchema}}."InstancesData" (
    "Id" uuid PRIMARY KEY,
    "InstanceId" uuid NOT NULL,
    "Version" character varying(180) NOT NULL,
    "VersionNo" bigint NOT NULL DEFAULT 0,
    "HistorySequence" integer NOT NULL DEFAULT 0,
    "ETag" character varying(26) NOT NULL,
    "DataHash" character varying(40) NOT NULL,
    "Data" jsonb NOT NULL,
    "EnteredAt" timestamp with time zone NOT NULL,
    "IsLatest" boolean NOT NULL DEFAULT FALSE
);

CREATE UNIQUE INDEX "UX_InstancesData_Instance_VersionNo"
    ON {{quotedSchema}}."InstancesData" ("InstanceId", "VersionNo");

CREATE UNIQUE INDEX "UX_InstancesData_Instance_IsLatest"
    ON {{quotedSchema}}."InstancesData" ("InstanceId")
    WHERE "IsLatest" = TRUE;

CREATE OR REPLACE FUNCTION {{quotedSchema}}.set_instance_data_version_and_latest()
RETURNS trigger AS $trigger$
DECLARE
    next_version_no bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext(NEW."InstanceId"::text));

    EXECUTE format(
        'SELECT COALESCE(MAX("VersionNo"), 0) + 1 FROM %I."InstancesData" WHERE "InstanceId" = $1',
        TG_TABLE_SCHEMA
    ) INTO next_version_no USING NEW."InstanceId";

    NEW."VersionNo" := next_version_no;

    IF NEW."IsLatest" THEN
        EXECUTE format(
            'UPDATE %I."InstancesData" SET "IsLatest" = FALSE WHERE "InstanceId" = $1 AND "IsLatest" = TRUE',
            TG_TABLE_SCHEMA
        ) USING NEW."InstanceId";
    END IF;

    RETURN NEW;
END;
$trigger$ LANGUAGE plpgsql;

CREATE TRIGGER trg_instancesdata_set_version_and_latest
    BEFORE INSERT ON {{quotedSchema}}."InstancesData"
    FOR EACH ROW
    EXECUTE FUNCTION {{quotedSchema}}.set_instance_data_version_and_latest();
""";

        await ExecuteAsync(sql);
        await ExecuteAsync(
            $"SET search_path = {quotedSchema};\n{AddInstanceDataConditionalBatchAppend.UpSql}");
    }

    private async Task<Baseline> SeedBaselineAsync(
        string schema,
        string data,
        Guid? instanceId = null)
    {
        var id = instanceId ?? Guid.NewGuid();
        var row = Row(data, true);
        await InsertAsync(schema, id, row);
        return new Baseline(id, row.DataId, row.ETag);
    }

    private async Task<StoredRow> InsertHeadAsync(string schema, Guid instanceId, string data)
    {
        var row = Row(data, true);
        await InsertAsync(schema, instanceId, row);
        return await ReadLatestAsync(schema, instanceId);
    }

    private async Task InsertAsync(string schema, Guid instanceId, InputRow row)
    {
        var quotedSchema = QuoteIdentifier(schema);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
INSERT INTO {quotedSchema}."InstancesData"
    ("Id", "InstanceId", "Version", "HistorySequence", "ETag", "DataHash", "Data", "EnteredAt", "IsLatest")
VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, $9);
""";
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, row.DataId);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, instanceId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, row.Version);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, row.HistorySequence);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, row.ETag);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, row.DataHash);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, row.Data.GetRawText());
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, row.EnteredAt);
        command.Parameters.AddWithValue(NpgsqlDbType.Boolean, row.IsLatest);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<FunctionRow>> CallFunctionAsync(
        string schema,
        Guid instanceId,
        Guid? expectedDataId,
        string? expectedEtag,
        IReadOnlyList<InputRow> rows)
    {
        var quotedSchema = QuoteIdentifier(schema);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""SELECT * FROM {quotedSchema}.try_append_instance_data_batch($1, $2, $3, $4);""";
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, instanceId);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = expectedDataId ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = expectedEtag ?? (object)DBNull.Value });
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(rows));

        var result = new List<FunctionRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new FunctionRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : NormalizeJson(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                reader.IsDBNull(9) ? null : reader.GetBoolean(9)));
        }

        return result;
    }

    private async Task<IReadOnlyList<StoredRow>> ReadRowsAsync(string schema, Guid instanceId)
    {
        var quotedSchema = QuoteIdentifier(schema);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT "Id", "Version", "VersionNo", "HistorySequence", "ETag", "DataHash",
       "Data"::text, "EnteredAt", "IsLatest"
FROM {quotedSchema}."InstancesData"
WHERE "InstanceId" = $1
ORDER BY "VersionNo";
""";
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, instanceId);

        var rows = new List<StoredRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new StoredRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                NormalizeJson(reader.GetString(6)),
                reader.GetDateTime(7),
                reader.GetBoolean(8)));
        }

        return rows;
    }

    private async Task<StoredRow> ReadLatestAsync(string schema, Guid instanceId)
    {
        return (await ReadRowsAsync(schema, instanceId)).Single(x => x.IsLatest);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static InputRow[] Rows(params (string Data, bool IsLatest)[] rows)
    {
        return rows.Select(x => Row(x.Data, x.IsLatest)).ToArray();
    }

    private static InputRow Row(string data, bool isLatest, string version = "1.0.0")
    {
        var now = DateTime.UtcNow;
        var enteredAt = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        return new InputRow(
            Guid.NewGuid(),
            version,
            0,
            Ulid.NewUlid().ToString(),
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant(),
            JsonDocument.Parse(data).RootElement.Clone(),
            enteredAt,
            isLatest);
    }

    private static string NormalizeJson(string json)
    {
        return JsonDocument.Parse(json).RootElement.GetRawText();
    }

    private static string QuoteIdentifier(string identifier)
    {
        return new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    }

    private sealed record Baseline(Guid InstanceId, Guid DataId, string ETag);

    private sealed record InputRow(
        Guid DataId,
        string Version,
        int HistorySequence,
        string ETag,
        string DataHash,
        JsonElement Data,
        DateTime EnteredAt,
        bool IsLatest);

    private sealed record FunctionRow(
        string Status,
        Guid? Id,
        string? Version,
        long? VersionNo,
        int? HistorySequence,
        string? ETag,
        string? DataHash,
        string? Data,
        DateTime? EnteredAt,
        bool? IsLatest);

    private sealed record StoredRow(
        Guid Id,
        string Version,
        long VersionNo,
        int HistorySequence,
        string ETag,
        string DataHash,
        string Data,
        DateTime EnteredAt,
        bool IsLatest);
}
