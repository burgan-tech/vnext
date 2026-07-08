using System;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Data;
using BBT.Workflow.Filtering;
using BBT.Workflow.Infrastructure.Instances;
using BBT.Workflow.Instances;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Integration tests for the instance filter engine (the model built by <see cref="InstanceQuery"/>,
/// translated to SQL by <see cref="InstanceFilterSqlBuilder"/> — the same SQL
/// <c>EfCoreInstanceRepository.FindByFilterAsync</c> runs). They seed a throwaway PostgreSQL with
/// deeply-nested JSON instance data and assert that complex filters (and/or/not, between, like, in,
/// nested attribute paths, columns) resolve the correct single instance, honoring First/Last ordering.
/// No real database is touched.
/// </summary>
public sealed class InstanceFilterQueryTests : IAsyncLifetime
{
    private const string Flow = "filter-test-flow";
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
        {
            await ctx.Database.EnsureCreatedAsync();
            await ApplyVersioningTrigger(ctx);
        }

        // Seed instances (createdAt increasing i1..i5). Nested JSON: address.city,
        // employment.department.name, plus numeric age/salary and an attribute-level status.
        await SeedAsync("u-lovelace", "waiting", "A", 1,
            Data("Ada", "Lovelace", "London", "Research", 36, 95000, "active"));
        await SeedAsync("u-hopper", "waiting", "A", 2,
            Data("Grace", "Hopper", "Paris", "Research", 45, 120000, "active"));
        await SeedAsync("u-byron", "waiting", "A", 3,
            Data("Ada", "Byron", "London", "Sales", 28, 40000, "active"));
        await SeedAsync("u-turing", "active-leave", "A", 4,
            Data("Alan", "Turing", "London", "Research", 41, 88000, "active"));
        await SeedAsync("u-old", "completed", "C", 5,
            Data("Ada", "Old", "London", "Research", 60, 70000, "cancelled"));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    // ---------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Eq_OnAttribute_ResolvesTheSingleMatch()
    {
        var key = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.surname", f => f.Eq("Hopper"))
            .Last());

        key.ShouldBe("u-hopper");
    }

    [Fact]
    public async Task Like_WithFirstVsLast_PicksOldestThenNewestMatch()
    {
        // name "Ada" matches u-lovelace(1), u-byron(3), u-old(5).
        var first = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.name", f => f.Like("Ada"))
            .OrderBy("createdAt").First());
        first.ShouldBe("u-lovelace");

        var last = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.name", f => f.Like("Ada"))
            .OrderByDescending("createdAt").Last());
        last.ShouldBe("u-lovelace"); // Last flips the desc order -> oldest again

        var newest = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.name", f => f.Like("Ada"))
            .OrderBy("createdAt").Last()); // Last on asc order -> newest
        newest.ShouldBe("u-old");
    }

    [Fact]
    public async Task Between_OnNumericAttribute_MatchesRangeAndTakesNewest()
    {
        // age in [30,46]: u-lovelace(36), u-hopper(45), u-turing(41). Newest = u-turing(4).
        var key = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.age", f => f.Between(30, 46))
            .OrderBy("createdAt").Last());

        key.ShouldBe("u-turing");
    }

    [Fact]
    public async Task Gt_OnNumericAttribute_UsesNumericComparison()
    {
        // salary > 100000: only u-hopper(120000). Salary is nested under employment.
        var key = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.employment.salary", f => f.Gt(100000))
            .Last());

        key.ShouldBe("u-hopper");
    }

    [Fact]
    public async Task In_OnAttribute_MatchesMembership()
    {
        var key = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.address.city", f => f.In("Paris"))
            .Last());

        key.ShouldBe("u-hopper");
    }

    [Fact]
    public async Task NestedAttributePath_FiltersOnDeepJson()
    {
        var key = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.employment.department.name", f => f.Eq("Sales"))
            .Last());

        key.ShouldBe("u-byron");
    }

    [Fact]
    public async Task AndOfColumnAndNestedAttributes_ResolvesCorrectly()
    {
        // status column 'A' AND city=London AND dept=Research -> u-lovelace(1), u-turing(4). Newest = u-turing.
        var key = await FindKeyAsync(InstanceQuery.Create()
            .Where("status", f => f.Eq("A"))
            .Where("attributes.address.city", f => f.Eq("London"))
            .Where("attributes.employment.department.name", f => f.Eq("Research"))
            .OrderBy("createdAt").Last());

        key.ShouldBe("u-turing");
    }

    [Fact]
    public async Task OrGroups_AndedTogether_ResolveNestedBoolean()
    {
        // (city=Paris OR city=London) AND (dept=Sales OR age>=60), First(oldest):
        //   city matches all; second group: u-byron(Sales) or u-old(age 60). Oldest = u-byron(3).
        var key = await FindKeyAsync(InstanceQuery.Create()
            .OrGroup(
                q => q.Where("attributes.address.city", f => f.Eq("Paris")),
                q => q.Where("attributes.address.city", f => f.Eq("London")))
            .OrGroup(
                q => q.Where("attributes.employment.department.name", f => f.Eq("Sales")),
                q => q.Where("attributes.age", f => f.Ge(60)))
            .OrderBy("createdAt").First());

        key.ShouldBe("u-byron");
    }

    [Fact]
    public async Task Not_ExcludesMatchingSubtree()
    {
        // surname=Lovelace AND NOT(city=Paris) -> u-lovelace (London).
        var key = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.surname", f => f.Eq("Lovelace"))
            .Not(q => q.Where("attributes.address.city", f => f.Eq("Paris")))
            .Last());

        key.ShouldBe("u-lovelace");
    }

    [Fact]
    public async Task OrderByNumericAttribute_SortsNumericallyNotLexicographically()
    {
        // Salaries: 95000, 120000, 40000, 88000, 70000. Text ordering sorts "120000" FIRST
        // (leading '1'), so an ascending Last() would wrongly pick 95000 (u-lovelace). Native
        // jsonb ordering must pick the true maximum, 120000 (u-hopper).
        var key = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.employment.salary", f => f.Gt(0))
            .OrderBy("attributes.employment.salary").Last());

        key.ShouldBe("u-hopper");
    }

    [Fact]
    public async Task NoMatch_ReturnsNull()
    {
        var key = await FindKeyAsync(InstanceQuery.Create()
            .Where("attributes.name", f => f.Eq("Nobody"))
            .Last());

        key.ShouldBeNull();
    }

    // ---------------------------------------------------------------------
    // Harness: mirrors EfCoreInstanceRepository.FindByFilterAsync exactly
    // (WHERE/ORDER built by InstanceFilterSqlBuilder; SELECT joins latest data; LIMIT 1).
    // ---------------------------------------------------------------------

    private async Task<string?> FindKeyAsync(InstanceFilter filter)
    {
        var builder = new InstanceFilterSqlBuilder();
        var where = builder.BuildWhere(filter.Root);
        var descending = filter.Selection == InstanceSelection.First
            ? filter.Order.Descending
            : !filter.Order.Descending;
        var order = InstanceFilterSqlBuilder.BuildOrderBy(filter.Order.Field, descending);

        var sql =
            "SELECT s.* FROM \"public\".\"Instances\" s " +
            "LEFT JOIN \"public\".\"InstancesData\" d ON d.\"InstanceId\" = s.\"Id\" AND d.\"IsLatest\" = true " +
            $"WHERE {where} ORDER BY {order} LIMIT 1";

        await using var ctx = CreateContext();
        var instance = await ctx.Instances
            .FromSqlRaw(sql, builder.Parameters.ToArray())
            .AsNoTracking()
            .FirstOrDefaultAsync();
        return instance?.Key;
    }

    private static string Data(string name, string surname, string city, string dept, int age, int salary, string status)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            name,
            surname,
            status,
            age,
            address = new { city, country = "UK" },
            employment = new { salary, department = new { name = dept } }
        });

    private async Task SeedAsync(string key, string currentState, string status, int order, string dataJson)
    {
        var instanceId = Guid.NewGuid();
        var createdAt = _base.AddMinutes(order);

        await using var ctx = CreateContext();

        // Tags via ARRAY[]::text[] and ExtraProperties as a parameter — avoid literal '{}' which EF's
        // raw-SQL builder would misread as a "{n}" placeholder. LastTouchedAt is a generated column
        // (GENERATED ALWAYS) so it is omitted and computed by PostgreSQL.
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"public\".\"Instances\" " +
            "(\"Id\",\"Key\",\"Flow\",\"FlowVersion\",\"CurrentState\",\"Status\",\"Tags\",\"ExtraProperties\",\"Incidents\",\"CreatedAt\") " +
            "VALUES ({0},{1},{2},'1.0.0',{3},{4},ARRAY[]::text[],{5},'[]'::jsonb,{6})",
            instanceId, key, Flow, currentState, status, "{}", createdAt);

        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"public\".\"InstancesData\" " +
            "(\"Id\",\"InstanceId\",\"Version\",\"HistorySequence\",\"ETag\",\"DataHash\",\"Data\",\"EnteredAt\",\"VersionNo\",\"IsLatest\") " +
            "VALUES ({0},{1},'1.0.0',0,{2},'hash',{3}::jsonb,{4},0,false)",
            Guid.NewGuid(), instanceId, Ulid.NewUlid().ToString(), dataJson, createdAt);
    }

    private WorkflowDbContext CreateContext()
    {
        // "Include Error Detail" surfaces the failing column/constraint in Postgres errors (test-only).
        var connectionString = _connectionString.TrimEnd(';') + ";Include Error Detail=true";
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema("public"));
    }

    private static async Task ApplyVersioningTrigger(WorkflowDbContext context)
    {
        await context.Database.ExecuteSqlRawAsync(@"
CREATE OR REPLACE FUNCTION set_instance_data_version_and_latest()
RETURNS trigger AS $$
DECLARE next_version_no bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext(NEW.""InstanceId""::text));
    SELECT COALESCE(MAX(""VersionNo""), 0) + 1 INTO next_version_no
      FROM ""InstancesData"" WHERE ""InstanceId"" = NEW.""InstanceId"";
    NEW.""VersionNo"" := next_version_no;
    UPDATE ""InstancesData"" SET ""IsLatest"" = FALSE
     WHERE ""InstanceId"" = NEW.""InstanceId"" AND ""IsLatest"" = TRUE;
    NEW.""IsLatest"" := TRUE;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;");

        await context.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_instancesdata_set_version_and_latest') THEN
        CREATE TRIGGER trg_instancesdata_set_version_and_latest
        BEFORE INSERT ON ""InstancesData""
        FOR EACH ROW EXECUTE FUNCTION set_instance_data_version_and_latest();
    END IF;
END;
$$;");
    }
}
