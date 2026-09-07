using System;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Runs the real migration chain against PostgreSQL the way <see cref="MultiSchemaMigrator{TContext}"/>
/// does (schema-aware SQL generator, per-schema history table): migrate up to the last pre-incident
/// migration, seed <c>Instances</c> rows whose legacy <c>"Incidents"</c> jsonb array holds incidents,
/// then migrate to the latest and assert that <c>MoveInstanceIncidentsToTable</c> +
/// <c>BackfillInstanceIncidents</c> copied every element into <c>InstanceIncidents</c>, raised
/// <c>HasActiveIncident</c> only where an unresolved incident exists, and left the legacy column in place.
/// </summary>
public sealed class InstanceIncidentsBackfillMigrationTests : IAsyncLifetime
{
    private const string PreIncidentTableMigration = "20260901065706_AddSubflowSettlementMarker";
    private const string Schema = "public";

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb").WithUsername("test").WithPassword("test")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Backfill_CopiesJsonbIncidentsIntoTable_AndRaisesFlagForUnresolved()
    {
        var withActive = Guid.NewGuid();
        var allResolved = Guid.NewGuid();
        var emptyArray = Guid.NewGuid();
        var nullColumn = Guid.NewGuid();
        var activeIncidentId = Guid.NewGuid();
        var resolvedIncidentId = Guid.NewGuid();

        await using (var ctx = CreateContext())
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreIncidentTableMigration);

            await InsertLegacyInstanceAsync(ctx, withActive, "with-active", $$"""
                [
                  {"id":"{{resolvedIncidentId}}","createdAt":"2026-08-01T10:00:00.1234567Z","state":"review","transition":"submit",
                   "task":"call-a","message":"first failure","stackTrace":"at A()","traceId":"trace-1","errorCode":"Task:Http:500",
                   "errorLayer":"Task","statusCode":500,"boundaryAction":"Retry","boundaryLevel":"Task","isResolved":true,
                   "resolvedAt":"2026-08-01T10:05:00Z","retryCount":2},
                  {"id":"{{activeIncidentId}}","createdAt":"2026-08-02T10:00:00Z","state":"review","transition":"submit",
                   "task":null,"message":"still broken","stackTrace":null,"traceId":null,"errorCode":"Task:Http:503",
                   "errorLayer":"Task","statusCode":503,"boundaryAction":"Abort","boundaryLevel":"Global","isResolved":false,
                   "resolvedAt":null,"retryCount":0}
                ]
                """);
            await InsertLegacyInstanceAsync(ctx, allResolved, "all-resolved", """
                [{"id":"7f0b7b5e-1111-4b7e-9c2f-000000000001","createdAt":"2026-08-03T00:00:00Z","state":"s","transition":"t",
                  "message":"m","errorCode":"E","errorLayer":"Pipeline","isResolved":true,"resolvedAt":"2026-08-03T00:01:00Z","retryCount":0}]
                """);
            await InsertLegacyInstanceAsync(ctx, emptyArray, "empty", "[]");
            await InsertLegacyInstanceAsync(ctx, nullColumn, "null-col", null);

            // The pre-incident-table generated column must have been computing the flag until now.
            (await ScalarAsync<bool>(ctx, $"SELECT \"HasActiveIncident\" FROM \"Instances\" WHERE \"Id\" = '{withActive}'")).ShouldBeTrue();

            await ctx.Database.MigrateAsync();
        }

        await using var verify = CreateContext();

        var rows = await verify.InstanceIncidents.AsNoTracking().OrderBy(i => i.CreatedAt).ToListAsync();
        rows.Count.ShouldBe(3);

        var resolved = rows.Single(i => i.Id == resolvedIncidentId);
        resolved.InstanceId.ShouldBe(withActive);
        resolved.IsResolved.ShouldBeTrue();
        resolved.ResolvedAt.ShouldNotBeNull();
        resolved.StatusCode.ShouldBe(500);
        resolved.RetryCount.ShouldBe(2);
        resolved.Task.ShouldBe("call-a");
        resolved.StackTrace.ShouldBe("at A()");
        resolved.BoundaryAction.ShouldBe("Retry");
        resolved.CreatedAt.ShouldBe(new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560), TimeSpan.FromMilliseconds(1));

        var active = rows.Single(i => i.Id == activeIncidentId);
        active.IsResolved.ShouldBeFalse();
        active.Task.ShouldBeNull();
        active.StackTrace.ShouldBeNull();
        active.ErrorCode.ShouldBe("Task:Http:503");

        rows.Count(i => i.InstanceId == allResolved).ShouldBe(1);
        rows.Any(i => i.InstanceId == emptyArray || i.InstanceId == nullColumn).ShouldBeFalse();

        var flags = await verify.Instances.AsNoTracking()
            .ToDictionaryAsync(i => i.Id, i => i.HasActiveIncident);
        flags[withActive].ShouldBeTrue();
        flags[allResolved].ShouldBeFalse();
        flags[emptyArray].ShouldBeFalse();
        flags[nullColumn].ShouldBeFalse();

        // Legacy column kept for this release (rollback + source of truth until the follow-up drop).
        (await ScalarAsync<long>(verify,
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'Instances' AND column_name = 'Incidents'"))
            .ShouldBe(1);
        // The generated column is gone; the real one is a plain boolean.
        (await ScalarAsync<string>(verify,
            "SELECT is_generated FROM information_schema.columns WHERE table_name = 'Instances' AND column_name = 'HasActiveIncident'"))
            .ShouldBe("NEVER");
        (await ScalarAsync<long>(verify,
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'Instances' AND indexname = 'IX_Instances_HasActiveIncident'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Backfill_IsIdempotent_WhenReplayed()
    {
        var instanceId = Guid.NewGuid();
        await using var ctx = CreateContext();
        await ctx.GetService<IMigrator>().MigrateAsync(PreIncidentTableMigration);
        await InsertLegacyInstanceAsync(ctx, instanceId, "replay", """
            [{"id":"7f0b7b5e-2222-4b7e-9c2f-000000000002","createdAt":"2026-08-03T00:00:00Z","state":"s","transition":"t",
              "message":"m","errorCode":"E","errorLayer":"Pipeline","isResolved":false,"retryCount":0}]
            """);
        await ctx.Database.MigrateAsync();

        // Replay the backfill body: ON CONFLICT DO NOTHING must keep the row count stable.
        var migration = ctx.GetService<IMigrationsAssembly>().Migrations.Keys
            .Single(k => k.EndsWith("_BackfillInstanceIncidents", StringComparison.Ordinal));
        var instance = ctx.GetService<IMigrationsAssembly>().CreateMigration(
            ctx.GetService<IMigrationsAssembly>().Migrations[migration], ctx.Database.ProviderName!);
        var sqlGenerator = ctx.GetService<IMigrationsSqlGenerator>();
        foreach (var command in sqlGenerator.Generate(instance.UpOperations, ctx.Model))
            await ctx.Database.ExecuteSqlRawAsync(command.CommandText);

        (await ctx.InstanceIncidents.CountAsync(i => i.InstanceId == instanceId)).ShouldBe(1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task InsertLegacyInstanceAsync(WorkflowDbContext ctx, Guid id, string key, string? incidentsJson)
    {
        // Parameters (not literals) for the json values: EF's raw-SQL builder reads a literal '{}' as a
        // "{n}" placeholder. The legacy column is NULL-able; a null json means SQL NULL.
        return incidentsJson is null
            ? ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Instances\" (\"Id\",\"Key\",\"Flow\",\"FlowVersion\",\"Status\",\"Tags\",\"ExtraProperties\",\"Incidents\",\"CreatedAt\") " +
                "VALUES ({0}, {1}, 'legacy-flow', '1.0.0', 'A', ARRAY[]::text[], {2}::jsonb, NULL, now())",
                id, key, "{}")
            : ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Instances\" (\"Id\",\"Key\",\"Flow\",\"FlowVersion\",\"Status\",\"Tags\",\"ExtraProperties\",\"Incidents\",\"CreatedAt\") " +
                "VALUES ({0}, {1}, 'legacy-flow', '1.0.0', 'A', ARRAY[]::text[], {2}::jsonb, {3}::jsonb, now())",
                id, key, "{}", incidentsJson);
    }

    private static async Task<T> ScalarAsync<T>(WorkflowDbContext ctx, string sql)
    {
        await using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T))!;
    }

    private WorkflowDbContext CreateContext()
    {
        // Same shape as MultiSchemaMigrator.MigrateSchemaAsync so the migration SQL is generated by the
        // production schema-aware generator (search_path injection for raw SQL included).
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsHistoryTable("__Workflow_Migrations", Schema))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .ReplaceService<IMigrationsSqlGenerator, MultiSchemaNpgsqlMigrationsSqlGenerator>()
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema(Schema));
    }
}
