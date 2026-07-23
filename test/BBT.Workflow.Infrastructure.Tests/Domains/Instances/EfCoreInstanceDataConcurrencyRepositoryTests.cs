using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Instances;
using BBT.Workflow.Migrations;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Integration tests for the schema-aware conditional-append repository path
/// (<see cref="IInstanceDataConcurrencyRepository"/> on <see cref="EfCoreInstanceRepository"/>):
/// ambient-transaction usage, EF change-tracker synchronization (returned rows attached as
/// Unchanged, stale head detached) and no-tracking latest-head reads.
/// </summary>
public sealed class EfCoreInstanceDataConcurrencyRepositoryTests : IAsyncLifetime
{
    private const string Tenant = "tenant_a";
    private const string QuotedTenant = "\"" + Tenant + "\"";

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;
    private DbContextOptions<WorkflowDbContext> _contextOptions = null!;

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

        _contextOptions = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .ReplaceService<IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>()
            .Options;

        await using (var context = CreateContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        await ApplyProductionVersioningTriggerAsync();
        await ApplyConditionalBatchAppendFunctionAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Fast_append_should_use_current_transaction_and_attach_returned_rows_as_unchanged()
    {
        var baseline = await SeedBaselineAsync();
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var repository = CreateRepository(context);
        var trackedStaleHead = await context.InstancesData
            .SingleAsync(x => x.InstanceId == baseline.InstanceId && x.IsLatest);

        var result = await repository.TryAppendDataAsync(
            baseline.InstanceId,
            baseline.DataId,
            baseline.ETag,
            new[] { PreparedAfterBaseline("""{"local":1}""") },
            CancellationToken.None);

        result.Status.ShouldBe(ConditionalAppendStatus.Applied);
        var appended = result.AppendedData.ShouldHaveSingleItem();
        context.Entry(appended).State.ShouldBe(EntityState.Unchanged);
        context.ChangeTracker.Entries<InstanceData>()
            .Count(x => x.State == EntityState.Added).ShouldBe(0);

        // The stale tracked head still claims IsLatest = true; it must be detached so it can
        // neither trigger orphan/delete detection nor write its stale flag back.
        context.Entry(trackedStaleHead).State.ShouldBe(EntityState.Detached);

        // Database-assigned VersionNo must be preserved on the synchronized entity.
        result.LatestData.ShouldNotBeNull();
        result.LatestData.Id.ShouldBe(appended.Id);
        result.LatestData.VersionNo.ShouldBe(2);
        result.LatestData.IsLatest.ShouldBeTrue();

        // Flushing the same context must not re-insert the raw-SQL-returned rows.
        await context.SaveChangesAsync();
        (await CountRowsAsync(context, baseline.InstanceId)).ShouldBe(2);

        await transaction.RollbackAsync();
        (await ReadRowCountInNewContextAsync(baseline.InstanceId)).ShouldBe(1);
    }

    [Fact]
    public async Task Latest_head_read_should_bypass_the_stale_tracked_entity()
    {
        var baseline = await SeedBaselineAsync();
        await using var context = CreateContext();
        var stale = await context.InstancesData
            .SingleAsync(x => x.InstanceId == baseline.InstanceId && x.IsLatest);
        var advancedHeadId = await AdvanceHeadInSeparateContextAsync(baseline.InstanceId);
        var repository = CreateRepository(context);

        var head = await repository.GetLatestDataHeadAsync(baseline.InstanceId, CancellationToken.None);

        head.ShouldNotBeNull();
        head.DataId.ShouldNotBe(stale.Id);
        head.DataId.ShouldBe(advancedHeadId);
        head.VersionNo.ShouldBe(2);
    }

    [Fact]
    public async Task Append_without_ambient_transaction_should_fail_before_calling_function()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        await Should.ThrowAsync<InvalidOperationException>(() => repository.TryAppendDataAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "etag",
            Array.Empty<PreparedInstanceData>(),
            CancellationToken.None));
    }

    [Fact]
    public async Task Idempotency_violation_should_surface_as_non_retryable_conflict_error()
    {
        var baseline = await SeedBaselineAsync();
        var prepared = PreparedAfterBaseline("""{"replayed":true}""");

        await using (var applyContext = CreateContext())
        await using (var applyTransaction = await applyContext.Database.BeginTransactionAsync())
        {
            var applyRepository = CreateRepository(applyContext);
            var applied = await applyRepository.TryAppendDataAsync(
                baseline.InstanceId, baseline.DataId, baseline.ETag,
                new[] { prepared }, CancellationToken.None);
            applied.Status.ShouldBe(ConditionalAppendStatus.Applied);
            await applyTransaction.CommitAsync();
        }

        // Same DataId with a mutated field raises instance_data_idempotency_violation (P0001);
        // it must surface as a non-retryable error, never as a retryable conflict.
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var repository = CreateRepository(context);
        var mutated = prepared with { ETag = Ulid.NewUlid().ToString() };

        var result = await repository.TryAppendDataAsync(
            baseline.InstanceId, baseline.DataId, baseline.ETag,
            new[] { mutated }, CancellationToken.None);

        result.Error.ShouldNotBeNull();
        result.Status.ShouldBe(ConditionalAppendStatus.Conflict);
        result.AppendedData.ShouldBeEmpty();
        result.LatestData.ShouldBeNull();
    }

    private WorkflowDbContext CreateContext()
    {
        return new WorkflowDbContext(_contextOptions, new StaticCurrentSchema(Tenant));
    }

    private IInstanceDataConcurrencyRepository CreateRepository(WorkflowDbContext context)
    {
        var dbContextProvider = Substitute.For<IAetherDbContextProvider<WorkflowDbContext>>();
        dbContextProvider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(context);

        return new EfCoreInstanceRepository(
            dbContextProvider,
            Substitute.For<IServiceProvider>(),
            Substitute.For<IWorkflowMetrics>(),
            Substitute.For<IRuntimeInfoProvider>(),
            Substitute.For<IDataSinkManager>(),
            new StaticCurrentSchema(Tenant),
            Substitute.For<ISchemaValidator>(),
            Options.Create(new WorkflowExecutionOptions()),
            NullLogger<EfCoreInstanceRepository>.Instance);
    }

    private async Task<BaselineRow> SeedBaselineAsync()
    {
        var instanceId = Guid.NewGuid();
        var dataId = Guid.NewGuid();
        var etag = Ulid.NewUlid().ToString();

        await using var context = CreateContext();
        context.Instances.Add(Instance.Create(instanceId, "test-flow", "1.0.0"));
        await context.SaveChangesAsync();

        await InsertRowAsync(context, instanceId, dataId, etag, """{"base":1}""");
        return new BaselineRow(instanceId, dataId, etag);
    }

    private async Task<Guid> AdvanceHeadInSeparateContextAsync(Guid instanceId)
    {
        var dataId = Guid.NewGuid();
        await using var context = CreateContext();
        await InsertRowAsync(context, instanceId, dataId, Ulid.NewUlid().ToString(), """{"remote":2}""");
        return dataId;
    }

    private static async Task InsertRowAsync(
        WorkflowDbContext context, Guid instanceId, Guid dataId, string etag, string dataJson)
    {
        await context.Database.ExecuteSqlRawAsync(
            $$"""
            INSERT INTO {{QuotedTenant}}."InstancesData"
                ("Id", "InstanceId", "Version", "HistorySequence", "ETag", "DataHash", "Data", "EnteredAt", "VersionNo", "IsLatest")
            VALUES ({0}, {1}, {2}, 0, {3}, {4}, {5}::jsonb, {6}, 0, true)
            """,
            dataId,
            instanceId,
            "1.0.0",
            etag,
            Sha1(dataJson),
            dataJson,
            DateTime.UtcNow);
    }

    private static PreparedInstanceData PreparedAfterBaseline(string dataJson)
    {
        var now = DateTime.UtcNow;
        // Truncate to microseconds so the value round-trips through PostgreSQL unchanged.
        var enteredAt = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        return new PreparedInstanceData(
            Guid.NewGuid(),
            "1.0.1",
            0,
            Ulid.NewUlid().ToString(),
            Sha1(dataJson),
            new JsonData(dataJson),
            enteredAt,
            IsLatest: true);
    }

    private static async Task<int> CountRowsAsync(WorkflowDbContext context, Guid instanceId)
    {
        return await context.InstancesData
            .AsNoTracking()
            .CountAsync(x => x.InstanceId == instanceId);
    }

    private async Task<int> ReadRowCountInNewContextAsync(Guid instanceId)
    {
        await using var context = CreateContext();
        return await CountRowsAsync(context, instanceId);
    }

    private async Task ApplyProductionVersioningTriggerAsync()
    {
        await ExecuteAsync(
            $$"""
            CREATE OR REPLACE FUNCTION {{QuotedTenant}}.set_instance_data_version_and_latest()
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
                BEFORE INSERT ON {{QuotedTenant}}."InstancesData"
                FOR EACH ROW
                EXECUTE FUNCTION {{QuotedTenant}}.set_instance_data_version_and_latest();
            """);
    }

    private async Task ApplyConditionalBatchAppendFunctionAsync()
    {
        await ExecuteAsync(
            $"SET search_path = {QuotedTenant};\n{AddInstanceDataConditionalBatchAppend.UpSql}");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Sha1(string data)
    {
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private sealed record BaselineRow(Guid InstanceId, Guid DataId, string ETag);
}
