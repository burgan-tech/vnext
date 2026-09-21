using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Exercises the production repository and complete EF model against PostgreSQL 18.6.
/// Each test owns a database inside its test container; no existing runtime stack is used.
/// </summary>
public sealed class InstanceListRepositoryIntegrationTests(AttributeIndexPostgresFixture fixture) : IAsyncLifetime,
    IClassFixture<AttributeIndexPostgresFixture>
{
    private string _connection = null!;
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        var database = "list_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.Postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await command.ExecuteNonQueryAsync();
        }
        _connection = new NpgsqlConnectionStringBuilder(fixture.Postgres.GetConnectionString()) { Database = database }.ConnectionString;
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        for (var i = 1; i <= 6; i++)
        {
            var instance = Instance.Create(Id(i), "list-fixture", i <= 3 ? "1.0.0" : "2.0.0", "row-" + i);
            instance.CreatedAt = CreatedAt;
            db.Instances.Add(instance);
            if (i == 3) continue; // A legal instance with no latest data must survive unfiltered listing.
            var amount = i switch { 1 => "20", 2 or 4 => "10", 5 => null, _ => "null" };
            var amountField = amount == null ? "" : "\"amount\":" + amount + ",";
            var json = "{" + amountField + "\"category\":\"" + (i <= 2 ? "A" : "B") + "\",\"when\":\"2026-01-0" + i + "T00:00:00Z\"}";
            var history = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0", new JsonData("{\"amount\":-100}"), false) { VersionNo = 1 };
            var latest = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.1", new JsonData(json), true) { VersionNo = 1 };
            instance.AcceptPersistedData(history);
            instance.AcceptPersistedData(latest);
        }
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _services.Dispose();
        // The class fixture disposes the isolated container and all per-test databases together.
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityPages_HydrateSelectedRows_KeepStableOrderAndHistoryMode(bool latestOnly)
    {
        var capture = new ReadCapture();
        await using var db = CreateContext(capture);
        var repository = CreateRepository(db, latestOnly);
        var first = await repository.GetPagedResultsWithGroupsAsync(1, 2, (GraphQLFilterRequest?)null);
        Assert.Equal(new[] { Id(1), Id(2) }, first.PagedList.Items.Select(row => row.Id));
        Assert.True(first.PagedList.HasNext);
        Assert.Null(first.Groups);
        Assert.Contains(capture.Commands, command => command.Contains("SELECT s.\"Id\" AS \"Value\""));
        Assert.Single(capture.HydrationIds);
        Assert.Equal(new[] { Id(1), Id(2) }, capture.HydrationIds[0]);
        foreach (var item in first.PagedList.Items)
        {
            Assert.Equal(latestOnly ? 1 : 2, item.DataList.Count);
            Assert.Equal(latestOnly, item.IsDataPartiallyLoaded);
            Assert.Equal("1.0.1", item.LatestData!.Version);
            Assert.True(item.LatestData.IsLatest);
            Assert.Equal(item.Id == Id(1) ? 20 : 10, item.LatestData.Data.JsonElement.GetProperty("amount").GetInt32());
        }
        Assert.Empty(db.ChangeTracker.Entries());

        var second = await repository.GetPagedResultsWithGroupsAsync(2, 2, (GraphQLFilterRequest?)null);
        Assert.Equal(new[] { Id(3), Id(4) }, second.PagedList.Items.Select(row => row.Id));
        Assert.Null(second.PagedList.Items.First().LatestData);
        Assert.True(second.PagedList.HasNext);
        var last = await repository.GetPagedResultsWithGroupsAsync(3, 2, (GraphQLFilterRequest?)null);
        Assert.Equal(new[] { Id(5), Id(6) }, last.PagedList.Items.Select(row => row.Id));
        Assert.False(last.PagedList.HasNext);
        var pastEnd = await repository.GetPagedResultsWithGroupsAsync(4, 2, (GraphQLFilterRequest?)null);
        Assert.Empty(pastEnd.PagedList.Items);
        Assert.False(pastEnd.PagedList.HasNext);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneAggregations_PreserveLegacyEmptyRepositoryResponse(bool parsed)
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db);
        const string aggregation = """{"count":true,"sum":"attributes.amount","avg":"attributes.amount","min":"attributes.amount","max":"attributes.amount"}""";
        var result = parsed
            ? await repository.GetPagedResultsWithGroupsAsync(2, 1, GraphQLFilterParser.ParseRequest(null, null, aggregation))
            : await repository.GetPagedResultsWithGroupsAsync(2, 1, (string?)null, aggregations: aggregation);
        Assert.Empty(result.PagedList.Items);
        Assert.False(result.PagedList.HasNext);
        Assert.Null(result.Groups);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GroupResults_PreserveCompleteGroupsAndLegacyEmptyResult()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db);
        const string grouping = """{"field":"attributes.category","aggregations":{"count":true,"sum":"attributes.amount"}}""";
        var grouped = await repository.GetPagedResultsWithGroupsAsync(9, 1, (string?)null, groupBy: grouping);
        Assert.Empty(grouped.PagedList.Items);
        Assert.NotNull(grouped.Groups);
        Assert.Equal(2, grouped.Groups.Count);
        Assert.Equal(new[] { "A", "B" }, grouped.Groups.Select(group => group.Keys["attributes.category"]));
        Assert.Equal(new long?[] { 2, 3 }, grouped.Groups.Select(group => group.Count));
        Assert.Equal(new decimal?[] { 30, 10 }, grouped.Groups.Select(group => group.Sum));
        var empty = await repository.GetPagedResultsWithGroupsAsync(1, 10,
            """{"attributes":{"amount":{"gt":999}}}""", groupBy: grouping);
        Assert.Null(empty.Groups);
        Assert.Empty(empty.PagedList.Items);
        Assert.False(empty.PagedList.HasNext);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutingOptions_AreReversibleAndPreserveMixedNullSortAndHistoryResults(bool latestOnly)
    {
        var cases = new (string? Filter, string? Sort)[]
        {
            (null, null),
            ("""{"or":[{"attributes":{"amount":{"gt":15}}},{"key":{"eq":"row-3"}}]}""", null),
            ("""{"not":{"attributes":{"amount":{"gt":15}}}}""", null),
            ("""{"attributes":{"amount":{"isnull":true}}}""", null),
            (null, """{"field":"attributes.amount","direction":"asc"}""")
        };
        foreach (var query in cases)
        {
            string? baseline = null;
            foreach (var route in new[]
            {
                new InstanceQueryOptions { IdentityPaging = false },
                new InstanceQueryOptions { IdentityPaging = true, LatestJoin = true },
                new InstanceQueryOptions { IdentityPaging = true, LatestJoin = false },
                new InstanceQueryOptions { IdentityPaging = true, DisabledSchemas = ["public"] }
            })
            {
                var capture = new ReadCapture();
                await using var db = CreateContext(capture);
                var repository = CreateRepository(db, latestOnly, new QueryMonitor(route));
                var response = await repository.GetPagedResultsWithGroupsAsync(1, 10, query.Filter, sort: query.Sort);
                var snapshot = JsonSerializer.Serialize(new
                {
                    Rows = response.PagedList.Items.Select(item => new
                    {
                        item.Id, Latest = item.LatestData?.Data.Json, History = item.DataList.Count, item.IsDataPartiallyLoaded
                    }),
                    response.PagedList.HasNext
                });
                baseline ??= snapshot;
                Assert.Equal(baseline, snapshot);
                var identityUsed = capture.Commands.Any(command => command.Contains("SELECT s.\"Id\" AS \"Value\""));
                Assert.Equal(route.IdentityPaging && route.DisabledSchemas.Length == 0, identityUsed);
                Assert.Empty(db.ChangeTracker.Entries());
            }
        }
    }

    [Fact]
    public async Task LiveQueryOptions_CanDisableAndReenableIdentityPagingWithinOneRepository()
    {
        var capture = new ReadCapture();
        var options = new QueryMonitor(new InstanceQueryOptions());
        await using var db = CreateContext(capture);
        var repository = CreateRepository(db, true, options);
        var first = await repository.GetPagedResultsWithGroupsAsync(1, 2, (GraphQLFilterRequest?)null);
        Assert.Contains(capture.Commands, sql => sql.Contains("SELECT s.\"Id\" AS \"Value\""));
        capture.Commands.Clear();
        options.CurrentValue = new InstanceQueryOptions { DisabledSchemas = ["public"] };
        var rollback = await repository.GetPagedResultsWithGroupsAsync(1, 2, (GraphQLFilterRequest?)null);
        Assert.DoesNotContain(capture.Commands, sql => sql.Contains("SELECT s.\"Id\" AS \"Value\""));
        Assert.Equal(first.PagedList.Items.Select(i => i.Id), rollback.PagedList.Items.Select(i => i.Id));
        options.CurrentValue = new InstanceQueryOptions { LatestJoin = false };
        capture.Commands.Clear();
        var restored = await repository.GetPagedResultsWithGroupsAsync(1, 2, (GraphQLFilterRequest?)null);
        Assert.Contains(capture.Commands, sql => sql.Contains("SELECT s.\"Id\" AS \"Value\""));
        Assert.Equal(first.PagedList.Items.Select(i => i.Id), restored.PagedList.Items.Select(i => i.Id));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task DeletionBetweenSelectionAndHydration_ReturnsSurvivorsWithoutRefillingPage()
    {
        var capture = new ReadCapture();
        capture.BeforeHydration = async () =>
        {
            await using var writer = CreateContext();
            await writer.Instances.Where(row => row.Id == Id(2)).ExecuteDeleteAsync();
        };
        await using var db = CreateContext(capture);
        var result = await CreateRepository(db).GetPagedResultsWithGroupsAsync(1, 2, (GraphQLFilterRequest?)null);
        Assert.True(capture.HydrationCallbackRan);
        Assert.Equal(new[] { Id(1) }, result.PagedList.Items.Select(row => row.Id));
        Assert.True(result.PagedList.HasNext); // The original sentinel still indicates a subsequent page.
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.False(await db.Instances.AnyAsync(row => row.Id == Id(2)));
    }

    [Fact]
    public async Task RepositoryReads_ObserveCurrentTransaction_AndRemainUsableAfterRollback()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"public\".\"InstancesData\" SET \"Data\" = jsonb_set(\"Data\", ARRAY['amount'], '50') WHERE \"InstanceId\" = {Id(1)} AND \"IsLatest\"");
            var page = await repository.GetPagedResultsWithGroupsAsync(1, 2, (GraphQLFilterRequest?)null);
            Assert.Equal(50, page.PagedList.Items.First().LatestData!.Data.JsonElement.GetProperty("amount").GetInt32());
            var aggregate = await GraphQLAggregationService.ExecuteAggregationAsync(db, null, new AggregationRequest { Sum = "attributes.amount" });
            Assert.Equal(70m, aggregate.Sum);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await transaction.RollbackAsync();
        }
        var restored = await GraphQLAggregationService.ExecuteAggregationAsync(db, null, new AggregationRequest { Sum = "attributes.amount" });
        Assert.Equal(40m, restored.Sum);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task TypedAggregation_UsesFullDatabaseModel()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"when":{"type":"string","format":"date-time","x-filterOperators":["gt"]},"amount":{"type":"number"}}}""");
        var metadata = SchemaFilterMetadataResolver.Resolve(schema.RootElement)!;
        var filter = GraphQLFilterParser.ParseFilter("""{"attributes":{"when":{"gt":"2026-01-03T00:00:00Z"}}}""")!;
        await using var db = CreateContext();
        var result = await GraphQLAggregationService.ExecuteAggregationAsync(db, filter,
            new AggregationRequest { Count = true, Sum = "attributes.amount" }, schemaContext: metadata);
        Assert.Equal(3L, result.Count);
        Assert.Equal(10m, result.Sum);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private WorkflowDbContext CreateContext(ReadCapture? capture = null)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(_connection);
        if (capture != null) options.AddInterceptors(capture);
        return new WorkflowDbContext(options.Options, new StaticCurrentSchema("public"));
    }

    private EfCoreInstanceRepository CreateRepository(WorkflowDbContext db, bool latestOnly = true, QueryMonitor? monitor = null)
    {
        var validator = Substitute.For<ISchemaValidator>();
        validator.ValidateSchemaAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<string>(0));
        validator.ValidateSchemaSync(Arg.Any<string?>()).Returns(call => call.ArgAt<string>(0));
        validator.ValidateTableName(Arg.Any<string?>()).Returns(call => call.ArgAt<string>(0));
        return new EfCoreInstanceRepository(new FixedProvider(db), _services, Substitute.For<IRuntimeInfoProvider>(),
            Substitute.For<IDataSinkManager>(), new StaticCurrentSchema("public"), validator,
            Options.Create(new WorkflowExecutionOptions { LatestOnlyInstanceLoading = latestOnly }),
            NullLogger<EfCoreInstanceRepository>.Instance, monitor ?? new QueryMonitor(new InstanceQueryOptions()));
    }

    private static Guid Id(int index) => Guid.Parse("00000000-0000-0000-0000-" + index.ToString("D12"));

    private sealed class FixedProvider(WorkflowDbContext context) : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(context);
    }

    private sealed class QueryMonitor(InstanceQueryOptions options) : IOptionsMonitor<InstanceQueryOptions>
    {
        public InstanceQueryOptions CurrentValue { get; set; } = options;
        public InstanceQueryOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<InstanceQueryOptions, string?> listener) => null;
    }

    private sealed class ReadCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<Guid[]> HydrationIds { get; } = [];
        public Func<Task>? BeforeHydration { get; set; }
        public bool HydrationCallbackRan { get; private set; }
        private bool _identitySeen;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            if (command.CommandText.Contains("SELECT s.\"Id\" AS \"Value\"")) _identitySeen = true;
            else if (_identitySeen && command.CommandText.Contains("InstancesData"))
            {
                foreach (DbParameter parameter in command.Parameters)
                    if (parameter.Value is IEnumerable<Guid> ids) HydrationIds.Add(ids.ToArray());
                if (BeforeHydration != null && !HydrationCallbackRan)
                {
                    HydrationCallbackRan = true;
                    await BeforeHydration();
                }
            }
            return result;
        }
    }
}
