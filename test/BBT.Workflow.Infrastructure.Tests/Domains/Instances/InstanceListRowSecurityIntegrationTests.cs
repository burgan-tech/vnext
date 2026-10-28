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
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Authorization.RowSecurity;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.RowSecurity;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
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
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Row security (vnext#1107) at the single repository merge point, against PostgreSQL 18.6: the planner's
/// predicate is ANDed with the caller's filter on every list route (identity paging, entity paging, groupBy)
/// and compiled by the same engine with the policy exemption. Each test owns a database in the container.
/// </summary>
/// <remarks>
/// Seed (latest data; r5 moved BR-003 → BR-001, so its history row says BR-003):
/// r1 BR-001 by bob · r2 BR-001 by carol · r3 BR-002 by alice · r4 BR-002 by bob (Data.createdBy "alice") ·
/// r5 BR-001 by erin · r6 BR-003 by dave.
/// </remarks>
public sealed class InstanceListRowSecurityIntegrationTests(AttributeIndexPostgresFixture fixture) : IAsyncLifetime,
    IClassFixture<AttributeIndexPostgresFixture>
{
    private string _connection = null!;
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Pinned compiled text of an unrestricted request (C-6): a request without RowPredicate must keep
    // producing exactly this SQL. Captured from the merge-point base commit (c3af9e3e).
    private const string BaselineIdentityPageSql =
        "SELECT s.\"Id\" AS \"Value\" FROM \"public\".\"Instances\" s JOIN \"public\".\"InstancesData\" _latest " +
        "ON _latest.\"InstanceId\" = s.\"Id\" AND _latest.\"IsLatest\" = true WHERE (\"Data\" @> @p0) " +
        "ORDER BY s.\"CreatedAt\" DESC, s.\"Id\" ASC LIMIT @p1 OFFSET @p2";

    private static readonly (int Row, string Branch, string CreatedBy, string Extra)[] Seed =
    [
        (1, "BR-001", "bob", """ "createdBy":"x","numStr":"10","num":10,"flag":true,"nul":null,"tags":["a","b"] """),
        (2, "BR-001", "carol", """ "numStr":10,"num":20,"flag":false,"nul":"v","tags":["b"] """),
        (3, "BR-002", "alice", """ "numStr":"20","num":10.0,"flag":"true","tags":"a" """),
        (4, "BR-002", "bob", """ "createdBy":"alice","numStr":"x","num":"10","flag":true,"nul":null """),
        (5, "BR-001", "erin", """ "num":30,"tags":[] """),
        (6, "BR-003", "dave", """ "numStr":"10","num":null,"flag":null,"nul":0 """)
    ];

    public async Task InitializeAsync()
    {
        var database = "rowsec_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.Postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await command.ExecuteNonQueryAsync();
        }
        _connection = new NpgsqlConnectionStringBuilder(fixture.Postgres.GetConnectionString()) { Database = database }.ConnectionString;
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        foreach (var (row, branch, createdBy, extra) in Seed)
        {
            var instance = Instance.Create(Id(row), "row-fixture", "1.0.0", "row-" + row);
            instance.CreatedAt = CreatedAt.AddMinutes(-row); // newest first ⇒ r1..r6 in default order
            instance.CreatedBy = createdBy;
            db.Instances.Add(instance);
            var historyBranch = row == 5 ? "BR-003" : branch;
            var history = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0",
                new JsonData("{\"branchCode\":\"" + historyBranch + "\"}"), false) { VersionNo = 1 };
            var latest = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.1",
                new JsonData("{\"branchCode\":\"" + branch + "\"," + extra.Trim() + "}"), true) { VersionNo = 1 };
            instance.AcceptPersistedData(history);
            instance.AcceptPersistedData(latest);
        }
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _services.Dispose();
        return Task.CompletedTask;
    }

    // ── merge guard ───────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> EmptyClauseShapes => new()
    {
        "top-empty", "nested-empty", "operatorless", "nested-object-operatorless", "nested-object-deep-operatorless"
    };

    [Theory]
    [MemberData(nameof(EmptyClauseShapes))]
    public async Task Merge_RowPredicateContainingEmptyNode_ThrowsBeforeSql(string shape)
    {
        var rowPredicate = shape switch
        {
            "top-empty" => new GraphQLFilterNode { PolicyExempt = true },
            "nested-empty" => new GraphQLFilterNode { PolicyExempt = true, Or = [Branch("BR-001"), new GraphQLFilterNode { PolicyExempt = true }] },
            // A nested object with no operator anywhere compiles to no SQL — empty, like an operator-less leaf.
            "nested-object-operatorless" => new GraphQLFilterNode
            {
                PolicyExempt = true, And = [Branch("BR-001"), Leaf("address", Nested("""{"city":{}}"""))]
            },
            "nested-object-deep-operatorless" => new GraphQLFilterNode
            {
                PolicyExempt = true,
                And = [Branch("BR-001"), Leaf("address", Nested("""{"city":{"eq":"x"},"zone":{"code":{}}}"""))]
            },
            _ => new GraphQLFilterNode { PolicyExempt = true, And = [Branch("BR-001"), Leaf("branchCode", new FieldCondition())] }
        };
        foreach (var identityPaging in new[] { true, false })
        {
            var capture = new ReadCapture();
            await using var db = CreateContext(capture);
            var repository = CreateRepository(db, new InstanceQueryOptions { IdentityPaging = identityPaging });

            var request = new GraphQLFilterRequest { RowPredicate = rowPredicate };
            var ex = await Should.ThrowAsync<FilterCompilationException>(() =>
                repository.GetPagedResultsWithGroupsAsync(1, 50, request));
            ex.Message.ShouldContain("row predicate contains an empty clause");
            await Should.ThrowAsync<FilterCompilationException>(() =>
                repository.GetPagedResultsWithGroupsAsync(1, 50, (string?)null, null, null, null, null, rowPredicate));

            capture.Commands.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Merge_EmptyCallerFilter_IsReplacedByRow_AndCallerRequestIsNotMutated()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db);
        var callerFilter = new GraphQLFilterNode();
        var request = new GraphQLFilterRequest { Filter = callerFilter, RowPredicate = Branch("BR-002") };

        var result = await repository.GetPagedResultsWithGroupsAsync(1, 50, request);

        result.PagedList.Items.Select(i => i.Id).ShouldBe([Id(3), Id(4)]);
        request.Filter.ShouldBeSameAs(callerFilter);
    }

    // ── compiled semantics ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatedByLeaf_CompilesToColumn_NotJson()
    {
        // r4 has Data.createdBy = "alice" but CreatedBy column = "bob": only r3 (column alice) is visible.
        var row = new GraphQLFilterNode { PolicyExempt = true, Attributes = new() { ["createdBy"] = new FieldCondition { Eq = "alice" } } };

        (await Ids(row, null, identityPaging: true)).ShouldBe([Id(3)]);
        (await Ids(row, null, identityPaging: false)).ShouldBe([Id(3)]);
    }

    [Fact]
    public async Task DefinitionB_MixedOr_IsUnion_OnPageIds_Request_AndAggregation()
    {
        // own branch (data) OR started by alice (column), under an enforcing schema that does not
        // declare branchCode filterable — the exemption lets the planner's leaf through.
        var definitionB = new GraphQLFilterNode
        {
            PolicyExempt = true,
            Or = [Branch("BR-001"), new GraphQLFilterNode { PolicyExempt = true, Attributes = new() { ["createdBy"] = new FieldCondition { Eq = "alice" } } }]
        };
        Guid[] expected = [Id(1), Id(2), Id(3), Id(5)];
        var schema = EnforcingSchemaWithoutBranchCode();

        (await Ids(definitionB, null, identityPaging: true, schema)).ShouldBe(expected);
        (await Ids(definitionB, null, identityPaging: false, schema)).ShouldBe(expected);

        await using var db = CreateContext();
        var grouped = await CreateRepository(db).GetPagedResultsWithGroupsAsync(1, 50, (string?)null,
            """{"field":"status","aggregations":{"count":true}}""", null, null, schema, definitionB);
        grouped.Groups.ShouldNotBeNull();
        grouped.Groups.Sum(g => g.Count ?? 0).ShouldBe(expected.Length);

        // The caller's own leaf on the same field is still refused under the same schema.
        await Should.ThrowAsync<SchemaFilterValidationException>(() =>
            CreateRepository(db).GetPagedResultsWithGroupsAsync(1, 50,
                """{"attributes":{"branchCode":{"eq":"BR-001"}}}""", null, null, null, schema, definitionB));
    }

    [Theory]
    [InlineData("BR-001", new[] { 1 })]
    [InlineData("BR-003", new int[0])]
    public async Task CallerOr_CannotEscapeRow(string ownBranch, int[] expectedRows)
    {
        const string callerOr = """{"or":[{"attributes":{"branchCode":{"eq":"BR-002"}}},{"key":{"eq":"row-1"}}]}""";
        var expected = expectedRows.Select(Id).ToArray();

        (await Ids(Branch(ownBranch), callerOr, identityPaging: true)).ShouldBe(expected);
        (await Ids(Branch(ownBranch), callerOr, identityPaging: false)).ShouldBe(expected);
    }

    [Fact]
    public async Task StringRoute_NewOverload_AppliesRow()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db);
        const string grouping = """{"field":"attributes.branchCode","aggregations":{"count":true}}""";

        var unrestricted = await repository.GetPagedResultsWithGroupsAsync(1, 50, (string?)null, groupBy: grouping);
        unrestricted.Groups.ShouldNotBeNull().Select(g => g.Name).ShouldBe(["BR-001", "BR-002", "BR-003"]);

        var restricted = await repository.GetPagedResultsWithGroupsAsync(1, 50, null, grouping, null, null, null, Branch("BR-001"));
        var group = restricted.Groups.ShouldNotBeNull().ShouldHaveSingleItem();
        group.Name.ShouldBe("BR-001");
        group.Count.ShouldBe(3); // r1, r2, r5 — evaluated on latest data (r5's history says BR-003)

        // The request-envelope form of the caller filter goes through the same shared parse and keeps the row.
        var envelope = await repository.GetPagedResultsWithGroupsAsync(1, 50,
            """{"filter":{"attributes":{"num":{"ge":20}}},"groupBy":{"field":"attributes.branchCode"}}""",
            grouping, null, null, null, Branch("BR-001"));
        envelope.Groups.ShouldNotBeNull().ShouldHaveSingleItem().Count.ShouldBe(2); // r2 (20), r5 (30)
    }

    public static TheoryData<string> FieldKinds => new() { "string", "numericString", "number", "boolean", "null", "missing", "array" };

    [Theory]
    [MemberData(nameof(FieldKinds))]
    public async Task RuleIn_SameIdsAsCallerIn(string fieldKind)
    {
        // U-10: a rule `in` (planner-shaped literals, exempt) selects exactly what the caller's `in`
        // (wire JSON, parsed) selects — the exemption changes no value semantics.
        var (field, values, json) = fieldKind switch
        {
            "string" => ("branchCode", new object?[] { "BR-001", "BR-003" }, "[\"BR-001\",\"BR-003\"]"),
            "numericString" => ("numStr", new object?[] { "10" }, "[\"10\"]"),
            // A parameter coerced to a number field arrives as decimal (planner TryCoerce); the wire sends long.
            "number" => ("num", new object?[] { 10m, 30m }, "[10,30]"),
            "boolean" => ("flag", new object?[] { true }, "[true]"),
            // Rows hold JSON null, "v", 0 or nothing; the planner never emits a null element (it drops the rule).
            "null" => ("nul", new object?[] { "v", "0" }, "[\"v\",\"0\"]"),
            "missing" => ("absent", new object?[] { "x" }, "[\"x\"]"),
            _ => ("tags", new object?[] { "a" }, "[\"a\"]")
        };
        var rule = new GraphQLFilterNode { PolicyExempt = true, Attributes = new() { [field] = new FieldCondition { In = values! } } };
        var caller = "{\"attributes\":{\"" + field + "\":{\"in\":" + json + "}}}";

        foreach (var identityPaging in new[] { true, false })
        {
            var callerOutcome = await Outcome(() => Ids(null, caller, identityPaging));
            var ruleOutcome = await Outcome(() => Ids(rule, null, identityPaging));
            ruleOutcome.ShouldBe(callerOutcome);
        }
    }

    [Fact]
    public async Task NoRowPredicate_SqlTextUnchanged()
    {
        var capture = new ReadCapture();
        await using var db = CreateContext(capture);
        var result = await CreateRepository(db).GetPagedResultsWithGroupsAsync(1, 50,
            """{"attributes":{"branchCode":{"eq":"BR-001"}}}""");

        result.PagedList.Items.Select(i => i.Id).ShouldBe([Id(1), Id(2), Id(5)]);
        Normalize(capture.Commands[0]).ShouldBe(BaselineIdentityPageSql);
    }

    // ── single GET probe (Task 7, U-5) ──────────────────────────────────────────────────────

    public static TheoryData<string> AllDefinitionPredicates => new()
    {
        "branch-BR-001", "branch-BR-002", "branch-BR-003", "createdBy-column", "definitionB-mixed-or",
        "definitionB-enforcing-schema", "in-branches", "not-branch", "and-branch-num"
    };

    private static (GraphQLFilterNode Predicate, SchemaFilterContext? Schema) DefinitionPredicate(string name) => name switch
    {
        "branch-BR-001" => (Branch("BR-001"), null),
        "branch-BR-002" => (Branch("BR-002"), null),
        "branch-BR-003" => (Branch("BR-003"), null),
        "createdBy-column" => (Leaf("createdBy", new FieldCondition { Eq = "alice" }), null),
        "definitionB-mixed-or" => (DefinitionB(), null),
        "definitionB-enforcing-schema" => (DefinitionB(), EnforcingSchemaWithoutBranchCode()),
        "in-branches" => (Leaf("branchCode", new FieldCondition { In = ["BR-002", "BR-003"] }), null),
        "not-branch" => (new GraphQLFilterNode { PolicyExempt = true, Not = Branch("BR-001") }, null),
        _ => (new GraphQLFilterNode { PolicyExempt = true, And = [Branch("BR-001"), Leaf("num", new FieldCondition { Ge = 20m })] }, null)
    };

    [Theory]
    [MemberData(nameof(AllDefinitionPredicates))]
    public async Task ListGetParity(string name)
    {
        var (predicate, schema) = DefinitionPredicate(name);
        foreach (var latestJoin in new[] { true, false })
        {
            var options = new InstanceQueryOptions { LatestJoin = latestJoin };
            var listIds = await Ids(predicate, null, identityPaging: true, schema);
            await using var db = CreateContext();
            var repository = CreateRepository(db, options);
            foreach (var id in Enumerable.Range(1, Seed.Length).Select(Id))
                (await repository.IsVisibleAsync(id, predicate, schema)).ShouldBe(listIds.Contains(id), $"{name} {id} latestJoin={latestJoin}");
        }
    }

    [Fact]
    public async Task Probe_UsesLatestData_NotHistory()
    {
        // r5's history row says BR-003, its latest row BR-001: visibility follows the latest row only.
        await using var db = CreateContext();
        var repository = CreateRepository(db);

        (await repository.IsVisibleAsync(Id(5), Branch("BR-001"), null)).ShouldBeTrue();
        (await repository.IsVisibleAsync(Id(5), Branch("BR-003"), null)).ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_PinsInstanceByTypedId_OneIdsOnlyQuery_UnknownIdIsInvisible()
    {
        var capture = new ReadCapture();
        await using var db = CreateContext(capture);
        var repository = CreateRepository(db);

        (await repository.IsVisibleAsync(Id(1), Branch("BR-001"), null)).ShouldBeTrue();
        capture.Commands.ShouldHaveSingleItem();
        var sql = Normalize(capture.Commands[0]);
        sql.ShouldStartWith("SELECT s.\"Id\" AS \"Value\" FROM \"public\".\"Instances\" s");
        sql.ShouldContain("s.\"Id\" = @p");
        sql.ShouldContain("LIMIT @p");

        (await repository.IsVisibleAsync(Guid.NewGuid(), Branch("BR-001"), null)).ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_RefusesEmptyRowPredicate_BeforeSql()
    {
        var capture = new ReadCapture();
        await using var db = CreateContext(capture);

        await Should.ThrowAsync<FilterCompilationException>(() =>
            CreateRepository(db).IsVisibleAsync(Id(1), new GraphQLFilterNode { PolicyExempt = true }, null));
        capture.Commands.ShouldBeEmpty();
    }

    // ── I-1: a comma inside an instance-column array value ─────────────────────────────────────

    [Fact]
    public async Task CommaIdentity_OnCreatedByIn_NeverReachesSql()
    {
        // Hazard pin: the instance-column compiler re-splits an array on ',', so one element "alice,bob"
        // compiled as is would match alice's AND bob's rows (r1, r3, r4). The planner must never emit it.
        var raw = Leaf("createdBy", new FieldCondition { In = ["alice,bob"] });
        (await Ids(raw, null, identityPaging: true)).ShouldBe([Id(1), Id(3), Id(4)]);

        // Planned: the createdBy rule is dropped (Coercion); only the other matched rule (BR-003) remains.
        var definition = JsonSerializer.Deserialize<RowSecurityDefinition>("""
          {"rules":[{"roles":["r"],"filter":{"createdBy":{"in":["$InstanceStarter"]}}},
                    {"roles":["r"],"filter":{"branchCode":{"eq":"BR-003"}}}]}
          """)!;
        var user = Substitute.For<ICurrentUser>();
        user.ActorUserName.Returns("alice,bob");
        var decision = RowSecurityPlanner.Plan(new RowSecurityPlanInput(definition,
            new CallerRoleResolution(["r"], CallerRoleOutcome.Resolved),
            new RequestRowParameterSource(new Dictionary<string, string[]>(), null, null, null, user), null,
            SchemaExposureMetadata.Empty, new RowSecurityOptions()));

        decision.Outcome.ShouldBe(RowSecurityOutcome.Filtered);
        decision.DroppedRules.ShouldHaveSingleItem().Reason.ShouldBe(RowRuleDropReasons.Coercion);
        foreach (var identityPaging in new[] { true, false })
        {
            var capture = new ReadCapture();
            await using var db = CreateContext(capture);
            var result = await CreateRepository(db, new InstanceQueryOptions { IdentityPaging = identityPaging })
                .GetPagedResultsWithGroupsAsync(1, 50, (string?)null, null, null, null, null, decision.Predicate);

            result.PagedList.Items.Select(i => i.Id).ShouldBe([Id(6)]);
            capture.Commands.ShouldNotBeEmpty();
            capture.ParameterValues.ShouldNotContain(v => v.Contains("alice") || v.Contains("bob"));
            capture.Commands.ShouldNotContain(c => c.Contains("CreatedBy\" IN", StringComparison.Ordinal));
        }
    }

    private static GraphQLFilterNode DefinitionB() => new()
    {
        PolicyExempt = true,
        Or = [Branch("BR-001"), Leaf("createdBy", new FieldCondition { Eq = "alice" })]
    };

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static GraphQLFilterNode Branch(string code) =>
        new() { PolicyExempt = true, Attributes = new() { ["branchCode"] = new FieldCondition { Eq = code } } };

    /// <summary>A condition carrying only nested field conditions (the JSON-extension-data shape the parser yields).</summary>
    private static FieldCondition Nested(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new FieldCondition
        {
            NestedConditions = document.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object)p.Value.Clone())
        };
    }

    private static GraphQLFilterNode Leaf(string field, FieldCondition condition) =>
        new() { PolicyExempt = true, Attributes = new() { [field] = condition } };

    private static SchemaFilterContext EnforcingSchemaWithoutBranchCode()
    {
        using var document = JsonDocument.Parse("""
          {"type":"object","properties":{
            "branchCode":{"type":"string"},
            "num":{"type":"number","x-filterOperators":["eq","gte","in"]}}}
          """);
        return SchemaFilterMetadataResolver.Resolve(document.RootElement)!;
    }

    private async Task<Guid[]> Ids(GraphQLFilterNode? rowPredicate, string? callerFilter, bool identityPaging,
        SchemaFilterContext? schema = null)
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, new InstanceQueryOptions { IdentityPaging = identityPaging });
        var result = rowPredicate == null
            ? await repository.GetPagedResultsWithGroupsAsync(1, 50, callerFilter, schemaContext: schema)
            : await repository.GetPagedResultsWithGroupsAsync(1, 50, callerFilter, null, null, null, schema, rowPredicate);
        return result.PagedList.Items.Select(i => i.Id).ToArray();
    }

    private static async Task<string> Outcome(Func<Task<Guid[]>> query)
    {
        try
        {
            return string.Join(",", await query());
        }
        catch (Exception ex)
        {
            return "throws:" + ex.GetType().Name;
        }
    }

    private static string Normalize(string sql) =>
        string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private WorkflowDbContext CreateContext(ReadCapture? capture = null)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(_connection);
        if (capture != null) options.AddInterceptors(capture);
        return new WorkflowDbContext(options.Options, new StaticCurrentSchema("public"));
    }

    private EfCoreInstanceRepository CreateRepository(WorkflowDbContext db, InstanceQueryOptions? options = null)
    {
        var validator = Substitute.For<ISchemaValidator>();
        validator.ValidateSchemaAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<string>(0));
        validator.ValidateSchemaSync(Arg.Any<string?>()).Returns(call => call.ArgAt<string>(0));
        validator.ValidateTableName(Arg.Any<string?>()).Returns(call => call.ArgAt<string>(0));
        return new EfCoreInstanceRepository(new FixedProvider(db), _services, Substitute.For<IRuntimeInfoProvider>(),
            Substitute.For<IDataSinkManager>(), new StaticCurrentSchema("public"), validator,
            Options.Create(new WorkflowExecutionOptions { LatestOnlyInstanceLoading = true }),
            NullLogger<EfCoreInstanceRepository>.Instance, new QueryMonitor(options ?? new InstanceQueryOptions()));
    }

    private static Guid Id(int index) => Guid.Parse("00000000-0000-0000-0000-" + index.ToString("D12"));

    private sealed class FixedProvider(WorkflowDbContext context) : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(context);
    }

    private sealed class QueryMonitor(InstanceQueryOptions options) : IOptionsMonitor<InstanceQueryOptions>
    {
        public InstanceQueryOptions CurrentValue { get; } = options;
        public InstanceQueryOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<InstanceQueryOptions, string?> listener) => null;
    }

    private sealed class ReadCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public List<string> ParameterValues { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            foreach (DbParameter parameter in command.Parameters)
                ParameterValues.Add(Convert.ToString(parameter.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            return ValueTask.FromResult(result);
        }
    }
}
