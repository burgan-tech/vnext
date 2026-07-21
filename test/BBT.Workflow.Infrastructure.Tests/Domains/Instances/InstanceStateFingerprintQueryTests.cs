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
/// Integration tests for the state-function fingerprint projection
/// (<see cref="EfCoreInstanceRepository.QueryStateFingerprintAsync"/>) against a real PostgreSQL:
/// verifies EF translatability of the single-row projection (including the correlated
/// <c>ChildCorrelations.Any</c> subquery over the SubFlowType value-converted column),
/// id-then-key identifier resolution, and most-recent-row ordering for duplicate keys.
/// </summary>
public sealed class InstanceStateFingerprintQueryTests : IAsyncLifetime
{
    private const string Flow = "fingerprint-test-flow";
    private const string FlowVersion = "1.0.0";

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
    public async Task ById_ProjectsFingerprintColumns()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "fp-by-id");
        instance.SetEffectiveState("review");
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, instance.Id.ToString());

        fingerprint.ShouldNotBeNull();
        fingerprint!.Id.ShouldBe(instance.Id);
        fingerprint.Key.ShouldBe("fp-by-id");
        fingerprint.EffectiveState.ShouldBe("review");
        fingerprint.Status.ShouldBe(InstanceStatus.Active);
        fingerprint.FlowVersion.ShouldBe(FlowVersion);
        fingerprint.HasActiveSubFlow.ShouldBeFalse();
    }

    [Fact]
    public async Task ByKey_ReturnsMostRecentRow()
    {
        var older = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "fp-dup-key");
        older.SetEffectiveState("review");
        await SeedAsync(older, createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var newer = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "fp-dup-key");
        newer.SetEffectiveState("approved");
        await SeedAsync(newer, createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, "fp-dup-key");

        fingerprint.ShouldNotBeNull();
        fingerprint!.Id.ShouldBe(newer.Id);
        fingerprint.EffectiveState.ShouldBe("approved");
    }

    [Fact]
    public async Task WithActiveSubFlowCorrelation_SetsHasActiveSubFlow()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "fp-subflow");
        instance.SetEffectiveState("review");
        instance.AddCorrelation(CreateCorrelation(instance.Id, subFlowType: "S"));
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, instance.Id.ToString());

        fingerprint.ShouldNotBeNull();
        fingerprint!.HasActiveSubFlow.ShouldBeTrue();
    }

    [Fact]
    public async Task WithCompletedOrSubProcessCorrelations_DoesNotSetHasActiveSubFlow()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "fp-no-active-subflow");
        instance.SetEffectiveState("review");

        var completedSubFlow = CreateCorrelation(instance.Id, subFlowType: "S");
        completedSubFlow.Completed();
        instance.AddCorrelation(completedSubFlow);
        instance.AddCorrelation(CreateCorrelation(instance.Id, subFlowType: "P"));
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, instance.Id.ToString());

        fingerprint.ShouldNotBeNull();
        fingerprint!.HasActiveSubFlow.ShouldBeFalse();
    }

    [Fact]
    public async Task UnknownIdentifier_ReturnsNull()
    {
        await using var ctx = CreateContext();

        var byGuid = await QueryAsync(ctx, Guid.NewGuid().ToString());
        var byKey = await QueryAsync(ctx, "fp-no-such-key");

        byGuid.ShouldBeNull();
        byKey.ShouldBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task<InstanceStateFingerprint?> QueryAsync(WorkflowDbContext ctx, string identifier) =>
        EfCoreInstanceRepository.QueryStateFingerprintAsync(
            ctx.Instances.AsNoTracking(), identifier, CancellationToken.None);

    private static InstanceCorrelation CreateCorrelation(Guid instanceId, string subFlowType) =>
        InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: instanceId,
            parentState: "review",
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: subFlowType,
            subFlowDomain: "sub-domain",
            subFlowName: "sub-flow",
            subFlowVersion: "1.0.0");

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

    private WorkflowDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema("public"));
    }
}
