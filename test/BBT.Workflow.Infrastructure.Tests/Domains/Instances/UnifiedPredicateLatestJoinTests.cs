using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions.GraphQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// The unified predicate (instance columns mixed with attribute conditions under OR / NOT, compiled with the
/// latest-data LEFT JOIN) guards attribute leaves with <c>_latest."Id" IS NOT NULL AND (…)</c> outside NOT, so
/// PostgreSQL can serve <c>"Data" @&gt; …</c> leaves from the GIN index on InstancesData (a CASE expression hid
/// them from the planner). These tests pin that it returns exactly the rows the former
/// <c>CASE WHEN _latest."Id" IS NULL THEN NULL …</c> guard returned, including for an instance without a data
/// row and for leaves under NOT / isNull. (Index use itself depends on volume; measured in the PR.)
/// Seeds mirror morph-idm session rows: retail (userName = actSub = sub), corporate (actSub + sub = firm),
/// a legacy corporate row without sub, an instance without any data row, and a closed (C) session.
/// </summary>
public sealed class UnifiedPredicateLatestJoinTests : IAsyncLifetime
{
    private const string Flow = "session";
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;
    private readonly DateTime _base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb").WithUsername("test").WithPassword("test")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await using (var ctx = CreateContext())
            await ctx.Database.EnsureCreatedAsync();

        await SeedAsync("retail", "A", 1, """{"userName":"11111111110","actSub":"11111111110","sub":"11111111110"}""");
        await SeedAsync("corporate", "A", 2, """{"userName":"22222222220","actSub":"22222222220","sub":"F1"}""");
        await SeedAsync("legacy-corporate", "A", 3, """{"userName":"33333333330-F1"}""");
        await SeedAsync("no-data", "A", 4, null);
        await SeedAsync("closed-retail", "C", 5, """{"userName":"11111111110","actSub":"11111111110","sub":"11111111110"}""");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    // ── Result sets (same as the former CASE guard) ─────────────────────────────────────────────

    [Fact]
    public async Task RetailUserShape_ReturnsOnlyTheOpenRetailSession()
    {
        var keys = await KeysAsync("""
            {"and":[{"status":{"in":["A","B"]}},{"createdAt":{"lt":"2027-01-01T00:00:00Z"}},
              {"or":[{"and":[{"attributes":{"actSub":{"eq":"11111111110"}}},{"attributes":{"sub":{"eq":"11111111110"}}}]},
                     {"attributes":{"userName":{"eq":"11111111110"}}}]}]}
            """);

        keys.ShouldBe(["retail"]);
    }

    [Fact]
    public async Task ScopeShapeWithEndsWith_ReturnsFirmSessionsIncludingLegacyRow()
    {
        var keys = await KeysAsync("""
            {"and":[{"status":{"in":["A","B"]}},
              {"or":[{"attributes":{"sub":{"eq":"F1"}}},{"attributes":{"userName":{"eq":"F1"}}},
                     {"attributes":{"userName":{"endswith":"-F1"}}}]}]}
            """);

        keys.ShouldBe(["corporate", "legacy-corporate"]);
    }

    [Fact]
    public async Task NotOverAttribute_StillExcludesTheInstanceWithoutData()
    {
        // NOT (NULL) = NULL drops the data-less instance; the guard under NOT must keep that (CASE form).
        var keys = await KeysAsync("""
            {"and":[{"status":{"eq":"A"}},{"not":{"attributes":{"userName":{"eq":"11111111110"}}}}]}
            """);

        keys.ShouldBe(["corporate", "legacy-corporate"]);
    }

    [Fact]
    public async Task IsNullOutsideNot_MatchesMissingKeyButNotTheInstanceWithoutData()
    {
        var keys = await KeysAsync("""
            {"or":[{"status":{"eq":"C"}},{"attributes":{"sub":{"isNull":true}}}]}
            """);

        keys.ShouldBe(["closed-retail", "legacy-corporate"]);
    }

    [Fact]
    public void UnifiedPredicate_OutsideNot_UsesIsNotNullGuard_AndUnderNot_KeepsCase()
    {
        var node = GraphQLFilterParser.ParseFilter("""
            {"and":[{"status":{"eq":"A"}},{"or":[{"attributes":{"sub":{"eq":"F1"}}},
              {"not":{"attributes":{"userName":{"eq":"x"}}}}]}]}
            """)!;

        var (sql, _) = GraphQLJsonFilterService.BuildInstanceQuery(node, "Data", "Instances", "public", useLatestJoin: true);

        sql.ShouldContain("(_latest.\"Id\" IS NOT NULL AND (");
        Regex.Matches(sql, "CASE WHEN _latest").Count.ShouldBe(1); // only the leaf under NOT
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static (string Sql, NpgsqlParameter[] Parameters) Build(string filterJson)
    {
        var node = GraphQLFilterParser.ParseFilter(filterJson)!;
        return GraphQLJsonFilterService.BuildInstanceQuery(node, "Data", "Instances", "public", useLatestJoin: true);
    }

    private async Task<List<string>> KeysAsync(string filterJson)
    {
        var (sql, parameters) = Build(filterJson);
        await using var ctx = CreateContext();
        // Parameterized: every operand is a {n} placeholder bound to an NpgsqlParameter; the throwaway
        // test container's "public" schema is the only interpolated identifier. nosemgrep
        var rows = await ctx.Instances.FromSqlRaw(sql, parameters).AsNoTracking().ToListAsync(); // nosemgrep
        return rows.Select(r => r.Key!).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private async Task SeedAsync(string key, string status, int order, string? dataJson)
    {
        var instanceId = Guid.NewGuid();
        var createdAt = _base.AddMinutes(order);
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"public\".\"Instances\" " +
            "(\"Id\",\"Key\",\"Flow\",\"FlowVersion\",\"CurrentState\",\"Status\",\"Tags\",\"ExtraProperties\",\"CreatedAt\",\"SubStateNotificationSeq\") " +
            "VALUES ({0},{1},{2},'1.0.0','active',{3},ARRAY[]::text[],{4},{5},0)",
            instanceId, key, Flow, status, "{}", createdAt);
        if (dataJson == null) return;
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"public\".\"InstancesData\" " +
            "(\"Id\",\"InstanceId\",\"Version\",\"ETag\",\"DataHash\",\"Data\",\"EnteredAt\",\"VersionNo\",\"IsLatest\") " +
            "VALUES ({0},{1},'1.0.0',{2},'hash',{3}::jsonb,{4},1,true)",
            Guid.NewGuid(), instanceId, Ulid.NewUlid().ToString(), dataJson, createdAt);
    }

    private WorkflowDbContext CreateContext()
    {
        var connectionString = _connectionString.TrimEnd(';') + ";Include Error Detail=true";
        var options = new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(connectionString).Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema("public"));
    }
}
