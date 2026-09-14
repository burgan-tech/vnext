using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

public sealed class AttributeIndexPostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder()
        .WithImage("postgres:18.6-bookworm").WithDatabase("indexes").WithUsername("test").WithPassword("test").Build();
    public Task InitializeAsync() => Postgres.StartAsync();
    public async Task DisposeAsync() => await Postgres.DisposeAsync();
}

public sealed class AttributeIndexIntegrationTests(AttributeIndexPostgresFixture fixture) : IClassFixture<AttributeIndexPostgresFixture>
{
    private static SchemaFilterContext Metadata()
    {
        using var document = JsonDocument.Parse("""
          {"type":"object","properties":{
            "amount":{"type":"number","x-indexed":true,"x-filterOperators":["eq","neq","gt","gte","lt","lte","between","in","nin","isNull"],"x-sortable":true},
            "name":{"type":"string","x-indexed":true,"x-filterOperators":["eq","neq","contains","startsWith","endsWith","in","nin","isNull"],"x-sortable":true},
            "when":{"type":"string","format":"date-time","x-indexed":true,"x-filterOperators":["gte","between"]}}}
          """);
        return SchemaFilterMetadataResolver.Resolve(document.RootElement)!;
    }

    private LegacyAttributeIndexFixture Service(bool enabled = true, string[]? disabled = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = fixture.Postgres.GetConnectionString(),
            ["AttributeIndexes:Enabled"] = enabled.ToString()
        }).Build();
        var settings = new AttributeIndexOptions { Enabled = enabled, DisabledFlows = disabled ?? [] };
        return new LegacyAttributeIndexFixture(configuration, new Monitor(settings), new StaticCurrentSchema("public"), new DefaultSchemaNameFormatter(),
            new MemoryCache(new MemoryCacheOptions()));
    }

    private async Task<string> SeedAsync()
    {
        var schema = "ix_" + Guid.NewGuid().ToString("N");
        await SqlAsync($$"""
          CREATE SCHEMA "{{schema}}";
          CREATE TABLE "{{schema}}"."Instances" ("Id" uuid PRIMARY KEY, "CreatedAt" timestamptz NOT NULL, "Status" text NOT NULL);
          CREATE TABLE "{{schema}}"."InstancesData" ("Id" uuid PRIMARY KEY, "InstanceId" uuid NOT NULL, "Data" jsonb NOT NULL, "IsLatest" boolean NOT NULL, "EnteredAt" timestamptz NOT NULL DEFAULT now());
          INSERT INTO "{{schema}}"."Instances" SELECT md5(i::text)::uuid, '2026-01-01Z', CASE WHEN i % 2 = 0 THEN 'A' ELSE 'C' END FROM generate_series(1, 200) i;
          INSERT INTO "{{schema}}"."InstancesData" ("Id", "InstanceId", "Data", "IsLatest")
          SELECT "Id", "Id", jsonb_build_object('amount', row_number() OVER (ORDER BY "Id"), 'name', 'İstanbul_' || "Id"::text, 'when', '2026-01-02T03:00:00+03:00'), true FROM "{{schema}}"."Instances";
          INSERT INTO "{{schema}}"."InstancesData" ("Id", "InstanceId", "Data", "IsLatest") SELECT md5('history' || "Id"::text)::uuid, "Id", '{"amount":-1}', false FROM "{{schema}}"."Instances";
          """);
        return schema;
    }

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.Postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Projections_PreserveResultsAndPagination_AndStaySchemaScoped()
    {
        var schema = await SeedAsync();
        var service = Service(); var metadata = Metadata();
        Assert.Empty(await service.GetReadyAsync(schema));
        await service.ReconcileAsync(schema, metadata);
        await service.ReconcileAsync(schema.Replace('_', '-'), metadata);
        var ready = await service.GetReadyAsync(schema);
        Assert.Equal(5, ready.Count);
        var indexed = new SchemaFilterContext(metadata.Fields) { ReadyIndexes = ready };
        await using var db = new QueryContext(fixture.Postgres.GetConnectionString());
        var filters = new[]
        {
            """{"attributes":{"amount":{"gt":190}}}""",
            """{"attributes":{"amount":{"between":[20,30]}}}""",
            """{"attributes":{"amount":{"eq":20}}}""",
            """{"attributes":{"amount":{"ne":20}}}""",
            """{"attributes":{"amount":{"in":["01","1","1.0"]}}}""",
            """{"attributes":{"amount":{"nin":[1,2]}}}""",
            """{"attributes":{"name":{"like":"İSTANBUL"}}}""",
            """{"attributes":{"name":{"startswith":"İstan"}}}""",
            """{"attributes":{"name":{"endswith":"a"}}}""",
            """{"attributes":{"when":{"ge":"2026-01-02T00:00:00Z"}}}""",
            """{"or":[{"status":{"eq":"A"}},{"attributes":{"amount":{"gt":190}}}]}""",
            """{"not":{"or":[{"status":{"eq":"A"}},{"attributes":{"amount":{"gt":190}}}]}}"""
        };
        foreach (var filter in filters)
        {
            var node = GraphQLFilterParser.ParseFilter(filter)!;
            var fallback = await db.Rows.ApplyGraphQLFilter(node, schema: schema, schemaContext: metadata).ToListAsync();
            var actual = await db.Rows.ApplyGraphQLFilter(node, schema: schema, schemaContext: indexed).ToListAsync();
            Assert.Equal(fallback.Select(r => r.Id), actual.Select(r => r.Id));
        }
        var request = new GraphQLFilterRequest { Filter = GraphQLFilterParser.ParseFilter(filters[3]), SchemaContext = indexed };
        var all = await db.Rows.ApplyGraphQLFilter(request.Filter!, schema: schema, schemaContext: indexed).ToListAsync();
        var page = await UnifiedFilterService.ExecuteRequestAsync(db, db.Rows, request, schema: schema, page: 2, pageSize: 7);
        Assert.Equal(all.Skip(7).Take(7).Select(r => r.Id), page.Data!.Select(r => r.Id));
        Assert.True(page.HasNextPage);
        Assert.Contains("LIMIT", db.Commands.Last());
        Assert.Contains("OFFSET", db.Commands.Last());
        var sorted = new GraphQLFilterRequest { SchemaContext = indexed, OrderBy = new OrderByRequest { Field = "attributes.amount", Direction = "desc" } };
        var indexedSort = await UnifiedFilterService.ExecuteRequestAsync(db, db.Rows, sorted, schema: schema, page: 2, pageSize: 7);
        sorted.SchemaContext = metadata;
        var fallbackSort = await UnifiedFilterService.ExecuteRequestAsync(db, db.Rows, sorted, schema: schema, page: 2, pageSize: 7);
        Assert.Equal(fallbackSort.Data!.Select(r => r.Id), indexedSort.Data!.Select(r => r.Id));
        var empty = await UnifiedFilterService.ExecuteRequestAsync(db, db.Rows, request, schema: schema, page: 30, pageSize: 7);
        Assert.Empty(empty.Data!); Assert.False(empty.HasNextPage);
        Assert.Empty(await Service(false).GetReadyAsync(schema));
        Assert.Empty(await Service(disabled: [schema]).GetReadyAsync(schema));
        Assert.Empty(await Service(disabled: [schema.Replace('_', '-')]).GetReadyAsync(schema));
        Assert.Empty(await Service().GetReadyAsync(await SeedAsync()));
        var mixed = GraphQLFilterParser.ParseFilter(filters[10])!;
        var expected = await db.Rows.ApplyGraphQLFilter(mixed, schema: schema).CountAsync();
        var aggregation = await GraphQLAggregationService.ExecuteAggregationAsync(db, mixed, new AggregationRequest { Count = true }, schema: schema, schemaContext: indexed);
        Assert.Equal(expected, aggregation.Count);
    }

    [Fact]
    public async Task NestedScalars_LegacyOperatorsAndAggregations_MatchJsonFallback()
    {
        var schema = await SeedAsync();
        using var document = JsonDocument.Parse("""
          {"type":"object","properties":{"profile":{"type":"object","properties":{
            "score":{"type":"integer","x-indexed":true,"x-filterOperators":["eq","neq","gt","gte","lt","lte","between","in","nin","isNull"],"x-sortable":true},
            "flag":{"type":"boolean","x-indexed":true,"x-filterOperators":["eq","neq","in","nin","isNull"]},
            "code":{"type":"string","x-indexed":true,"x-filterOperators":["eq","neq","contains","startsWith","endsWith","in","nin","isNull"]}}}}}
          """);
        var metadata = SchemaFilterMetadataResolver.Resolve(document.RootElement)!;
        await SqlAsync($$"""
          UPDATE "{{schema}}"."InstancesData" SET "Data" = "Data" || jsonb_build_object('profile',
            CASE WHEN ("Data"->>'amount')::int % 5 = 0 THEN '{}'::jsonb ELSE jsonb_build_object(
              'score', CASE WHEN ("Data"->>'amount')::int % 7 = 0 THEN NULL ELSE ("Data"->>'amount')::int % 3 END,
              'flag', ("Data"->>'amount')::int % 2 = 0,
              'code', CASE WHEN ("Data"->>'amount')::int % 2 = 0 THEN '01' ELSE 'İstanbul_%' END) END)
          WHERE "IsLatest";
          """);
        var service = Service();
        await service.ReconcileAsync(schema, metadata);
        var indexed = new SchemaFilterContext(metadata.Fields) { ReadyIndexes = await service.GetReadyAsync(schema) };
        await using var db = new QueryContext(fixture.Postgres.GetConnectionString());
        var filters = new[]
        {
            "profile.score=eq:1", "profile.score=ne:1", "profile.score=gt:1", "profile.score=ge:1",
            "profile.score=lt:1", "profile.score=le:1", "profile.score=between:0,1",
            "profile.score=in:01,1.0,1", "profile.score=nin:0,2", "profile.score=isnull:true",
            "profile.score=isnull:false", "profile.flag=eq:true", "profile.flag=ne:false",
            "profile.flag=in:true,false", "profile.code=in:01,1.0", "profile.code=like:%",
            "profile.code=startswith:İstan", "profile.code=endswith:_"
        };
        foreach (var filter in filters)
        {
            var fallback = await db.Rows.ApplyJsonFilters(filter, schema: schema, schemaContext: metadata).ToListAsync();
            var legacy = await db.Rows.ApplyJsonFilters(filter, schema: schema, schemaContext: indexed).ToListAsync();
            var node = FilterFormatDetector.ConvertLegacyToGraphQL(filter)!;
            var graph = await db.Rows.ApplyGraphQLFilter(node, schema: schema, schemaContext: indexed).ToListAsync();
            Assert.Equal(fallback.Select(r => r.Id), legacy.Select(r => r.Id));
            Assert.Equal(fallback.Select(r => r.Id), graph.Select(r => r.Id));
        }
        var aggregations = GraphQLFilterParser.ParseAggregations("""{"count":true,"sum":"attributes.profile.score","avg":"attributes.profile.score","min":"attributes.profile.code","max":"attributes.profile.code"}""")!;
        // Match request-scoped DbContexts; the existing aggregation service disposes its connection.
        await using var jsonAggregationDb = new QueryContext(fixture.Postgres.GetConnectionString());
        await using var indexedAggregationDb = new QueryContext(fixture.Postgres.GetConnectionString());
        var jsonAggregate = await GraphQLAggregationService.ExecuteAggregationAsync(jsonAggregationDb, null, aggregations, schema: schema, schemaContext: metadata);
        var indexedAggregate = await GraphQLAggregationService.ExecuteAggregationAsync(indexedAggregationDb, null, aggregations, schema: schema, schemaContext: indexed);
        Assert.Equal(JsonSerializer.Serialize(jsonAggregate), JsonSerializer.Serialize(indexedAggregate));
        var group = GraphQLFilterParser.ParseGroupBy("""{"field":"attributes.profile.score","aggregations":{"count":true}}""")!;
        await using var jsonGroupDb = new QueryContext(fixture.Postgres.GetConnectionString());
        await using var indexedGroupDb = new QueryContext(fixture.Postgres.GetConnectionString());
        var jsonGroups = await GraphQLAggregationService.ExecuteGroupByAsync(jsonGroupDb, null, group, schema: schema, schemaContext: metadata);
        var indexedGroups = await GraphQLAggregationService.ExecuteGroupByAsync(indexedGroupDb, null, group, schema: schema, schemaContext: indexed);
        Assert.Equal(JsonSerializer.Serialize(jsonGroups), JsonSerializer.Serialize(indexedGroups));
    }

    [Fact]
    public async Task InvalidHistory_RollsBackAndLeavesJsonPathAvailable()
    {
        var schema = await SeedAsync();
        await SqlAsync($$"""UPDATE "{{schema}}"."InstancesData" SET "Data" = '{"amount":"bad"}' WHERE NOT "IsLatest";""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ReconcileAsync(schema, Metadata()));
        Assert.Empty(await Service().GetReadyAsync(schema));
    }

    [Fact]
    public async Task MixedNot_PreservesSqlNullSemantics()
    {
        var schema = await SeedAsync();
        await SqlAsync($$"""
          UPDATE "{{schema}}"."InstancesData" SET "Data" = '{}' WHERE "IsLatest";
          """);
        await using var db = new QueryContext(fixture.Postgres.GetConnectionString());
        var filter = GraphQLFilterParser.ParseFilter("""{"not":{"or":[{"status":{"eq":"A"}},{"attributes":{"amount":{"gt":10}}}]}}""")!;
        // NOT(false OR NULL) is NULL, so missing amount must not turn into a match.
        Assert.Empty(await db.Rows.ApplyGraphQLFilter(filter, schema: schema).ToListAsync());
    }

    [Fact]
    public async Task DateGeneration_IsIndependentOfSessionSettings_AndTracksUpdates()
    {
        var schema = await SeedAsync();
        await Service().ReconcileAsync(schema, Metadata());
        var column = new AttributeIndexDefinition("when", "timestamptz").ColumnName;
        await SqlAsync($$"""
          SET TIME ZONE 'America/Los_Angeles'; SET DateStyle = 'SQL, DMY';
          UPDATE "{{schema}}"."InstancesData" SET "Data" = jsonb_set("Data", ARRAY['when'], '"2026-01-02T00:00:00Z"') WHERE "IsLatest";
          DO $check$ BEGIN
          IF EXISTS (SELECT 1 FROM "{{schema}}"."InstancesData" WHERE "IsLatest" AND "{{column}}" <> make_timestamptz(2026,1,2,0,0,0,'UTC')) THEN
            RAISE EXCEPTION 'Timestamp changed with session settings'; END IF;
          END $check$;
          UPDATE "{{schema}}"."InstancesData" SET "IsLatest" = false;
          DO $check$ BEGIN
          IF EXISTS (SELECT 1 FROM "{{schema}}"."InstancesData" WHERE "{{column}}" IS NOT NULL) THEN
            RAISE EXCEPTION 'Historical values must not pollute latest projection statistics'; END IF;
          END $check$;
          """);
        await using var db = new QueryContext(fixture.Postgres.GetConnectionString());
        Assert.Empty(await db.Rows.ApplyGraphQLFilter(GraphQLFilterParser.ParseFilter("""{"attributes":{"amount":{"gt":0}}}""")!, schema: schema).ToListAsync());
    }

    [Fact]
    public async Task AdvisoryLock_RejectsConcurrentMaintenance()
    {
        var schema = await SeedAsync();
        await using var connection = new NpgsqlConnection(fixture.Postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key, 0))", connection);
        command.Parameters.AddWithValue("key", "attribute-indexes:" + schema);
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ReconcileAsync(schema, Metadata()));
        command.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@key, 0))";
        await command.ExecuteNonQueryAsync();
        await Service().ReconcileAsync(schema, Metadata());
    }

    [Fact]
    public async Task RetiredProjection_DoesNotRejectNewType_AndCanBeReactivated()
    {
        var schema = await SeedAsync();
        await Service().ReconcileAsync(schema, Metadata());
        var fields = new Dictionary<string, SchemaFieldMetadata>(Metadata().Fields)
        {
            ["amount"] = new() { Type = "string", Indexed = true, FilterOperators = ["in"] }
        };
        await Service().ReconcileAsync(schema, new SchemaFilterContext(fields));
        var numeric = new AttributeIndexDefinition("amount", "numeric");
        Assert.DoesNotContain(numeric.Key, await Service().GetReadyAsync(schema));
        await SqlAsync($$"""
            UPDATE "{{schema}}"."InstancesData" SET "Data" = jsonb_set("Data", ARRAY['amount'], '"changed-type"');
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ReconcileAsync(schema, Metadata()));
        await SqlAsync($$"""
            UPDATE "{{schema}}"."InstancesData" SET "Data" = jsonb_set("Data", ARRAY['amount'], '123');
            """);
        await Service().ReconcileAsync(schema, Metadata());
        Assert.Contains(numeric.Key, await Service().GetReadyAsync(schema));
    }

    [Fact]
    public async Task ReservedColumnCollision_IsNotSilentlyActivated()
    {
        var schema = await SeedAsync();
        var column = new AttributeIndexDefinition("amount", "text").ColumnName;
        await SqlAsync($"ALTER TABLE \"{schema}\".\"InstancesData\" ADD COLUMN \"{column}\" text GENERATED ALWAYS AS ('wrong') STORED");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ReconcileAsync(schema, Metadata()));
        Assert.Empty(await Service().GetReadyAsync(schema));
    }

    [Fact]
    public async Task Cancellation_ReleasesLockAndLeavesNoPartiallyReadyCatalog()
    {
        var schema = await SeedAsync();
        using var cancellation = new System.Threading.CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ReconcileAsync(schema, Metadata(), cancellation.Token));
        Assert.Empty(await Service().GetReadyAsync(schema));
        await Service().ReconcileAsync(schema, Metadata());
    }

    [Fact]
    public async Task IdentityPages_JoinMatchesScalarPredicates_WithMissingLatestNullsAndStableTextSort()
    {
        var schema = await SeedAsync();
        await SqlAsync($$"""
            CREATE UNIQUE INDEX ON "{{schema}}"."InstancesData" ("InstanceId") WHERE "IsLatest";
            DELETE FROM "{{schema}}"."InstancesData" WHERE "InstanceId" = md5('1')::uuid;
            UPDATE "{{schema}}"."InstancesData" SET "Data" = '{}' WHERE "InstanceId" = md5('2')::uuid;
            UPDATE "{{schema}}"."InstancesData" SET "Data" = '{"amount":null}' WHERE "InstanceId" = md5('3')::uuid;
            UPDATE "{{schema}}"."InstancesData" SET "Data" = '{"amount":"02"}' WHERE "InstanceId" = md5('4')::uuid;
            """);
        var metadata = Metadata();
        await Service().ReconcileAsync(schema, metadata);
        await using var db = new QueryContext(fixture.Postgres.GetConnectionString());
        var filters = new string?[]
        {
            null,
            """{"attributes":{"amount":{"isnull":true}}}""",
            """{"attributes":{"amount":{"nin":[2,3]}}}""",
            """{"or":[{"status":{"eq":"A"}},{"attributes":{"amount":{"isnull":true}}}]}""",
            """{"not":{"or":[{"status":{"eq":"A"}},{"attributes":{"amount":{"isnull":true}}}]}}""",
            """{"and":[{"or":[{"status":{"eq":"A"}},{"attributes":{"amount":{"gt":190}}}]},{"not":{"attributes":{"amount":{"eq":2}}}}]}"""
        };
        foreach (var ready in new[] { new HashSet<string>(), await Service().GetReadyAsync(schema) })
        foreach (var filter in filters)
        foreach (var direction in new[] { "asc", "desc" })
        {
            var context = new SchemaFilterContext(metadata.Fields) { ReadyIndexes = ready };
            var request = new GraphQLFilterRequest
            {
                Filter = filter == null ? null : GraphQLFilterParser.ParseFilter(filter),
                OrderBy = new OrderByRequest { Field = "attributes.amount", Direction = direction },
                SchemaContext = context
            };
            var legacyOrder = GraphQLJsonFilterService.BuildOrderByClause(request.OrderBy, schema, schemaContext: context);
            var all = await db.Rows.ApplyGraphQLFilter(request.Filter ?? new GraphQLFilterNode(),
                schema: schema, schemaContext: context, orderByClause: legacyOrder, offset: 0, limit: 1000).ToListAsync();
            foreach (var page in new[] { 1, 2, 30 })
            {
                var actual = await UnifiedFilterService.ExecutePageIdsAsync(db, request, schema, page, 7);
                Assert.Equal(all.Skip((page - 1) * 7).Take(7).Select(r => r.Id), actual.Ids);
                Assert.Equal(all.Count > page * 7, actual.HasNext);
                Assert.DoesNotContain("SELECT s.*", db.Commands.Last());
                Assert.Contains("LIMIT", db.Commands.Last());
            }
        }
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ListOrderMigration_IsSchemaScopedIdempotent_AndKeepsExistingEquivalentIndex()
    {
        var schema = await SeedAsync();
        var other = await SeedAsync();
        var migration = new BBT.Workflow.Migrations.AddInstanceListOrderIndex();
        var up = string.Join(";", migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Select(o => o.Sql));
        var down = string.Join(";", migration.DownOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Select(o => o.Sql));
        await SqlAsync($$"""
            SET search_path TO "{{schema}}";
            CREATE INDEX existing_list_order ON "Instances" ("CreatedAt" DESC, "Id");
            {{up}}
            {{up}}
            {{down}}
            DO $$ BEGIN
              IF to_regclass('existing_list_order') IS NULL OR to_regclass('"IX_Instances_CreatedAt_Id"') IS NOT NULL THEN
                RAISE EXCEPTION 'existing equivalent index was not reused';
              END IF;
            END $$;
            SET search_path TO "{{other}}";
            {{up}}
            {{up}}
            DO $$ BEGIN
              IF to_regclass('"IX_Instances_CreatedAt_Id"') IS NULL THEN RAISE EXCEPTION 'index missing'; END IF;
            END $$;
            {{down}}
            DO $$ BEGIN
              IF to_regclass('"IX_Instances_CreatedAt_Id"') IS NOT NULL THEN RAISE EXCEPTION 'owned index not removed'; END IF;
            END $$;
            """);
    }

    private sealed class Monitor(AttributeIndexOptions value) : IOptionsMonitor<AttributeIndexOptions>
    {
        public AttributeIndexOptions CurrentValue => value;
        public AttributeIndexOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AttributeIndexOptions, string?> listener) => null;
    }

    private sealed class QueryContext(string connection) : DbContext
    {
        public List<string> Commands { get; } = [];
        public DbSet<Row> Rows => Set<Row>();
        protected override void OnConfiguring(DbContextOptionsBuilder builder) => builder.UseNpgsql(connection).AddInterceptors(new Capture(Commands));
        protected override void OnModelCreating(ModelBuilder builder) => builder.Entity<Row>().ToTable("Instances");
    }
    private sealed class Capture(List<string> commands) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            System.Threading.CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Row
    {
        public Guid Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Status { get; set; } = "";
    }
}
