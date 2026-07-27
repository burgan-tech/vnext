using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Integration tests for the data-function fingerprint projection
/// (<see cref="EfCoreInstanceRepository.QueryDataFingerprintAsync"/>) against a real PostgreSQL:
/// verifies the latest-data ETag subquery (IsLatest row only, index-backed), id-then-key
/// identifier resolution, and null semantics for data-less instances.
/// </summary>
public sealed class InstanceDataFingerprintQueryTests : IAsyncLifetime
{
    private const string Flow = "data-fingerprint-test-flow";
    private const string FlowVersion = "2.0.0";

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

        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ById_ProjectsLatestDataEtagFlowVersionAndEffectiveState()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "dfp-by-id");
        instance.SetEffectiveState("review");
        await SeedAsync(instance);
        var latestEtag = await SeedDataRowAsync(instance.Id, version: "1.0.1", isLatest: true);
        await SeedDataRowAsync(instance.Id, version: "1.0.0", isLatest: false);

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, instance.Id.ToString());

        fingerprint.ShouldNotBeNull();
        fingerprint!.Id.ShouldBe(instance.Id);
        fingerprint.Key.ShouldBe("dfp-by-id");
        fingerprint.LatestDataEtag.ShouldBe(latestEtag);
        fingerprint.FlowVersion.ShouldBe(FlowVersion);
        fingerprint.EffectiveState.ShouldBe("review");
        fingerprint.HasActiveSubFlow.ShouldBeFalse();
    }

    [Fact]
    public async Task WithActiveSubFlowCorrelation_SetsHasActiveSubFlow()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "dfp-subflow");
        instance.AddCorrelation(InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: instance.Id,
            parentState: "review",
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: "S",
            subFlowDomain: "sub-domain",
            subFlowName: "sub-flow",
            subFlowVersion: "1.0.0"));
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, instance.Id.ToString());

        fingerprint.ShouldNotBeNull();
        fingerprint!.HasActiveSubFlow.ShouldBeTrue();
    }

    [Fact]
    public async Task ByKey_ReturnsMostRecentRow()
    {
        var older = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "dfp-dup-key");
        await SeedAsync(older, createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedDataRowAsync(older.Id, version: "1.0.0", isLatest: true);

        var newer = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "dfp-dup-key");
        await SeedAsync(newer, createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var newerEtag = await SeedDataRowAsync(newer.Id, version: "1.0.0", isLatest: true);

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, "dfp-dup-key");

        fingerprint.ShouldNotBeNull();
        fingerprint!.Id.ShouldBe(newer.Id);
        fingerprint.LatestDataEtag.ShouldBe(newerEtag);
    }

    [Fact]
    public async Task WithoutDataRows_LatestDataEtagIsNull()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "dfp-no-data");
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, instance.Id.ToString());

        fingerprint.ShouldNotBeNull();
        fingerprint!.LatestDataEtag.ShouldBeNull();
    }

    [Fact]
    public async Task UnknownIdentifier_ReturnsNull()
    {
        await using var ctx = CreateContext();

        (await QueryAsync(ctx, Guid.NewGuid().ToString())).ShouldBeNull();
        (await QueryAsync(ctx, "dfp-no-such-key")).ShouldBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task<InstanceDataFingerprint?> QueryAsync(WorkflowDbContext ctx, string identifier) =>
        EfCoreInstanceRepository.QueryDataFingerprintAsync(
            ctx.Instances.AsNoTracking(), identifier, CancellationToken.None);

    private async Task SeedAsync(Instance instance, DateTime? createdAt = null)
    {
        await using var ctx = CreateContext();
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();

        if (createdAt is not null)
        {
            // CreatedAt is audit-stamped on save; pin it afterwards so duplicate-key
            // ordering assertions are deterministic.
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE \"public\".\"Instances\" SET \"CreatedAt\" = {0} WHERE \"Id\" = {1}",
                createdAt.Value, instance.Id);
        }
    }

    /// <summary>
    /// Inserts an InstancesData row via raw SQL (bypasses the domain aggregate and the
    /// production versioning trigger, which EnsureCreated does not install) so IsLatest can
    /// be controlled explicitly per row. Returns the row's ETag.
    /// </summary>
    private async Task<string> SeedDataRowAsync(Guid instanceId, string version, bool isLatest)
    {
        var etag = Ulid.NewUlid().ToString();
        await using var ctx = CreateContext();
        // Data passed as a parameter — a literal '{}' would be misread as a {n} placeholder
        // by the raw-SQL builder (see InstanceFilterQueryTests.SeedAsync).
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"public\".\"InstancesData\" " +
            "(\"Id\",\"InstanceId\",\"Version\",\"HistorySequence\",\"ETag\",\"DataHash\",\"Data\",\"EnteredAt\",\"VersionNo\",\"IsLatest\") " +
            "VALUES ({0},{1},{2},0,{3},'hash',{4}::jsonb,{5},{6},{7})",
            Guid.NewGuid(), instanceId, version, etag, "{}", DateTime.UtcNow, isLatest ? 2 : 1, isLatest);
        return etag;
    }

    private WorkflowDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema("public"));
    }
}
