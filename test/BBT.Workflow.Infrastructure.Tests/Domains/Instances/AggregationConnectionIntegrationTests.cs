using System;
using System.Data;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.Schemas;
using System.Text.Json;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Verifies raw aggregation commands respect EF connection, transaction and timeout ownership
/// against an isolated PostgreSQL container. Each test owns its schema and lock transaction.
/// </summary>
public sealed class AggregationConnectionIntegrationTests(AttributeIndexPostgresFixture fixture)
    : IClassFixture<AttributeIndexPostgresFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AggregationAndGrouping_AllowSubsequentEfQueries_AndPreserveCallerOpenConnection(bool callerOpened)
    {
        var schema = await SeedAsync();
        await using var db = CreateContext();
        var connection = db.Database.GetDbConnection();
        if (callerOpened) await db.Database.OpenConnectionAsync();

        try
        {
            var aggregation = await GraphQLAggregationService.ExecuteAggregationAsync(db, null,
                new AggregationRequest { Count = true, Sum = "attributes.amount" }, schema: schema);
            Assert.Equal(2L, aggregation.Count);
            Assert.Equal(30m, aggregation.Sum);
            Assert.Equal(callerOpened ? ConnectionState.Open : ConnectionState.Closed, connection.State);
            Assert.Equal(2, await CountRowsAsync(db, schema));

            var groups = await GraphQLAggregationService.ExecuteGroupByAsync(db, null,
                new GroupByRequest { Field = "attributes.category", Aggregations = new AggregationRequest { Count = true } }, schema: schema);
            Assert.Single(groups);
            Assert.Equal(2L, groups[0].Aggregations!.Count);
            Assert.Equal(callerOpened ? ConnectionState.Open : ConnectionState.Closed, connection.State);
            Assert.Equal(2, await CountRowsAsync(db, schema));
        }
        finally
        {
            if (callerOpened) await db.Database.CloseConnectionAsync();
        }
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregation_SeesUncommittedWrites_AndLeavesTransactionUsableForRollback(bool grouped)
    {
        var schema = await SeedAsync();
        await using var db = CreateContext();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync($$"""
                INSERT INTO "{{schema}}"."InstancesData" ("Data", "IsLatest")
                VALUES (jsonb_build_object('amount', 40, 'category', 'retail'), true)
                """);
            Assert.Equal(3L, await ExecuteCountAsync(db, schema, grouped));
            Assert.Same(transaction, db.Database.CurrentTransaction);
            Assert.Equal(ConnectionState.Open, db.Database.GetDbConnection().State);
            Assert.Equal(3, await CountRowsAsync(db, schema));
            await transaction.RollbackAsync();
        }

        Assert.Equal(2, await CountRowsAsync(db, schema));
        Assert.Equal(2L, await ExecuteCountAsync(db, schema, grouped));
        await using var observer = CreateContext();
        Assert.Equal(2, await CountRowsAsync(observer, schema));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregation_InheritsEfCommandTimeout_AndConnectionRemainsReusable(bool grouped)
    {
        var schema = await SeedAsync();
        await using var db = CreateContext();
        db.Database.SetCommandTimeout(1);
        await using var blocker = new NpgsqlConnection(fixture.Postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"LOCK TABLE \"{schema}\".\"InstancesData\" IN ACCESS EXCLUSIVE MODE", blocker, transaction);
        await command.ExecuteNonQueryAsync();
        using var safetyCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var exception = await Assert.ThrowsAsync<NpgsqlException>(() => ExecuteCountAsync(db, schema, grouped, safetyCancellation.Token));
            Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.False(safetyCancellation.IsCancellationRequested);
            Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Assert.Equal(2, await CountRowsAsync(db, schema));
        Assert.Equal(2L, await ExecuteCountAsync(db, schema, grouped));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregation_CancellationWhileWaitingForLock_ReleasesConnectionForNextQuery(bool grouped)
    {
        var schema = await SeedAsync();
        await using var db = CreateContext();
        db.Database.SetCommandTimeout(30);
        await using var blocker = new NpgsqlConnection(fixture.Postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"LOCK TABLE \"{schema}\".\"InstancesData\" IN ACCESS EXCLUSIVE MODE", blocker, transaction);
        await command.ExecuteNonQueryAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteCountAsync(db, schema, grouped, cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Assert.Equal(2, await CountRowsAsync(db, schema));
        Assert.Equal(2L, await ExecuteCountAsync(db, schema, grouped));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParsedCountField_CountsOnlyNonNullLatestValues(bool envelope)
    {
        var schema = await SeedAsync();
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync($$"""
            INSERT INTO "{{schema}}"."InstancesData" ("Data", "IsLatest") VALUES
              (jsonb_build_object('amount', NULL), true), (jsonb_build_object('other', 1), true)
            """);
        AggregationRequest fieldRequest;
        if (envelope)
        {
            Assert.True(GraphQLFilterParser.TryParseRequest("""{"aggregations":{"count":"attributes.amount"}}""", out var request));
            fieldRequest = request!.Aggregations!;
        }
        else fieldRequest = GraphQLFilterParser.ParseAggregations("""{"count":"attributes.amount"}""")!;
        var countField = await GraphQLAggregationService.ExecuteAggregationAsync(db, null, fieldRequest, schema: schema);
        var countAll = await GraphQLAggregationService.ExecuteAggregationAsync(db, null,
            GraphQLFilterParser.ParseAggregations("""{"count":true}""")!, schema: schema);
        Assert.Equal(2L, countField.Count);
        Assert.Equal(4L, countAll.Count);
    }

    private async Task<string> SeedVersionedAsync(string? version)
    {
        var schema = "ver_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.Postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($$"""
            CREATE SCHEMA "{{schema}}";
            CREATE TABLE "{{schema}}"."Instances" ("Id" int PRIMARY KEY, "FlowVersion" text);
            CREATE TABLE "{{schema}}"."InstancesData" ("InstanceId" int, "Data" jsonb, "IsLatest" bool);
            INSERT INTO "{{schema}}"."Instances" VALUES (1, @version);
            INSERT INTO "{{schema}}"."InstancesData" VALUES (1, jsonb_build_object('amount', 10, 'category', 'a-known'), true)
            """, connection);
        command.Parameters.AddWithValue("version", NpgsqlTypes.NpgsqlDbType.Text, (object?)version ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return schema;
    }

    [Theory]
    [InlineData("gt", false, 50)]
    [InlineData("gt", true, 50)]
    [InlineData("between", false, 20)]
    [InlineData("between", true, 20)]
    public async Task TimestampFilteringAndSum_PreserveTypedSemanticsAndDisabledModeRejection(string operation, bool enforcement, int expectedSum)
    {
        var schema = await SeedVersionedAsync("1.0.0");
        await using (var setup = CreateContext())
            await setup.Database.ExecuteSqlRawAsync($$"""
                ALTER TABLE "{{schema}}"."Instances" ADD COLUMN "CreatedAt" timestamptz DEFAULT '2026-01-01T00:00:00Z';
                UPDATE "{{schema}}"."InstancesData" SET "Data" = "Data" || jsonb_build_object('when', '2026-01-01T00:00:00Z');
                INSERT INTO "{{schema}}"."Instances" ("Id", "FlowVersion") VALUES (2, '2.0.0'), (3, '3.0.0');
                INSERT INTO "{{schema}}"."InstancesData" VALUES
                  (2, jsonb_build_object('amount', 20, 'when', '2026-01-03T03:00:00+03:00'), true),
                  (3, jsonb_build_object('amount', 30, 'when', '2026-01-05T00:00:00Z'), true),
                  (1, jsonb_build_object('amount', 999, 'when', '2026-01-03T00:00:00Z'), false)
                """);
        using var master = JsonDocument.Parse("""
            {"type":"object","properties":{"when":{"type":"string","format":"date-time","x-filterOperators":["gt","between"]},"amount":{"type":"number"}}}
            """);
        var metadata = SchemaFilterMetadataResolver.Resolve(master.RootElement)!;
        var context = new SchemaFilterContext(metadata.Fields) { EnforceFiltering = enforcement };
        var value = operation == "gt" ? "\"2026-01-02T00:00:00Z\"" : "[\"2026-01-02T00:00:00Z\",\"2026-01-04T00:00:00Z\"]";
        var filter = GraphQLFilterParser.ParseFilter("{\"attributes\":{\"when\":{\"" + operation + "\":" + value + "}}}")!;
        await using var db = new VersionQueryContext(fixture.Postgres.GetConnectionString());
        if (!enforcement)
        {
            // Existing disabled-enforcement mode resolves comparison types as numeric. Preserve its
            // rejection in both phases rather than silently changing date/cast semantics in this fix.
            var discoveryFailure = Assert.Throws<ArgumentException>(() => db.Rows.ApplyGraphQLFilter(filter, schema: schema, schemaContext: context));
            var aggregateFailure = await Assert.ThrowsAsync<ArgumentException>(() => GraphQLAggregationService.ExecuteAggregationAsync(db, filter,
                new AggregationRequest { Sum = "attributes.amount" }, schema: schema, schemaContext: context));
            Assert.Equal(discoveryFailure.Message, aggregateFailure.Message);
            Assert.Contains("numeric", discoveryFailure.Message);
            return;
        }
        var result = await GraphQLAggregationService.ExecuteAggregationAsync(db, filter,
            new AggregationRequest { Sum = "attributes.amount" }, schema: schema, schemaContext: context);
        Assert.Equal((decimal)expectedSum, result.Sum);
    }

    private sealed class VersionQueryContext(string connection) : DbContext
    {
        public DbSet<VersionRow> Rows => Set<VersionRow>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseNpgsql(connection);
        protected override void OnModelCreating(ModelBuilder model) => model.Entity<VersionRow>().ToTable("Instances");
    }

    private sealed class VersionRow
    {
        public int Id { get; set; }
        public string? FlowVersion { get; set; }
    }

    private DbContext CreateContext() => new(new DbContextOptionsBuilder()
        .UseNpgsql(fixture.Postgres.GetConnectionString()).Options);

    private async Task<string> SeedAsync()
    {
        var schema = "agg_" + Guid.NewGuid().ToString("N");
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync($$"""
            CREATE SCHEMA "{{schema}}";
            CREATE TABLE "{{schema}}"."InstancesData" ("Data" jsonb NOT NULL, "IsLatest" boolean NOT NULL);
            INSERT INTO "{{schema}}"."InstancesData" ("Data", "IsLatest") VALUES
              (jsonb_build_object('amount', 10, 'category', 'retail'), true),
              (jsonb_build_object('amount', 20, 'category', 'retail'), true),
              (jsonb_build_object('amount', -1000, 'category', 'history'), false);
            """);
        return schema;
    }

    private static Task<int> CountRowsAsync(DbContext db, string schema) => db.Database
        .SqlQueryRaw<int>($"SELECT COUNT(*)::int AS \"Value\" FROM \"{schema}\".\"InstancesData\" WHERE \"IsLatest\"")
        .SingleAsync();

    private static async Task<long?> ExecuteCountAsync(DbContext db, string schema, bool grouped, CancellationToken cancellationToken = default)
    {
        if (!grouped)
            return (await GraphQLAggregationService.ExecuteAggregationAsync(db, null,
                new AggregationRequest { Count = true }, schema: schema, cancellationToken: cancellationToken)).Count;
        var groups = await GraphQLAggregationService.ExecuteGroupByAsync(db, null,
            new GroupByRequest { Field = "attributes.category", Aggregations = new AggregationRequest { Count = true } },
            schema: schema, cancellationToken: cancellationToken);
        return Assert.Single(groups).Aggregations!.Count;
    }
}
