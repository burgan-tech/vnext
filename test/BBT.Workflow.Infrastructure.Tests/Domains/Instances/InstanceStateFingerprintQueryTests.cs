using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    /// <summary>
    /// The correlation aggregates must count the full set — active and completed — and must translate to
    /// SQL (COUNT / COUNT-with-predicate / MAX over nullable timestamp columns).
    /// </summary>
    [Fact]
    public async Task ProjectsCorrelationAggregatesOverActiveAndCompletedRows()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "fp-correlation-aggregates");
        instance.SetEffectiveState("review");

        var completedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var stateChangedAt = new DateTime(2026, 3, 4, 5, 6, 6, DateTimeKind.Utc);

        var completed = CreateCorrelation(instance.Id, subFlowType: "S");
        completed.ApplyTerminalOutcome(SubItemTerminalOutcome.Completed, completedAt);
        instance.AddCorrelation(completed);

        var active = CreateCorrelation(instance.Id, subFlowType: "S");
        active.UpdateSubFlowState("sub-review", stateChangedAt);
        instance.AddCorrelation(active);

        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, instance.Id.ToString());

        fingerprint.ShouldNotBeNull();
        fingerprint!.CorrelationCount.ShouldBe(2);
        fingerprint.CompletedCorrelationCount.ShouldBe(1);
        fingerprint.LastCorrelationCompletedAt.ShouldBe(completedAt);
        fingerprint.LastSubFlowStateChangedAt.ShouldBe(stateChangedAt);
    }

    /// <summary>
    /// No correlations at all must yield zero counts and null timestamps — SQL <c>MAX</c> over an empty
    /// set and LINQ-to-Objects <c>Max</c> over a nullable sequence must agree on null rather than one
    /// throwing or returning a default.
    /// </summary>
    [Fact]
    public async Task WithoutCorrelations_ProjectsZeroCountsAndNullTimestamps()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "fp-no-correlations");
        instance.SetEffectiveState("review");
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var fingerprint = await QueryAsync(ctx, instance.Id.ToString());

        fingerprint.ShouldNotBeNull();
        fingerprint!.CorrelationCount.ShouldBe(0);
        fingerprint.CompletedCorrelationCount.ShouldBe(0);
        fingerprint.LastCorrelationCompletedAt.ShouldBeNull();
        fingerprint.LastSubFlowStateChangedAt.ShouldBeNull();
    }

    /// <summary>
    /// Regression guard for endless cache invalidation. The long-poll fast path builds the fingerprint
    /// from the projection query while the full-build path builds it from the loaded aggregate plus the
    /// separately-read correlation list. If the two disagree on a single member, the ETag computed on the
    /// full path never matches the one the fast path validates against and every poll re-builds.
    /// Runs with a mixed correlation set (active, completed, state-advanced) to exercise every member.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionAndFromInstance_ProduceIdenticalFingerprints(bool withCorrelations)
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, $"fp-parity-{withCorrelations}");
        instance.SetEffectiveState("review");

        if (withCorrelations)
        {
            var completed = CreateCorrelation(instance.Id, subFlowType: "S");
            completed.ApplyTerminalOutcome(
                SubItemTerminalOutcome.Faulted, new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc));
            instance.AddCorrelation(completed);

            var advanced = CreateCorrelation(instance.Id, subFlowType: "P");
            advanced.UpdateSubFlowState("child-state", new DateTime(2026, 5, 6, 7, 8, 10, DateTimeKind.Utc));
            instance.AddCorrelation(advanced);

            instance.AddCorrelation(CreateCorrelation(instance.Id, subFlowType: "S"));
        }

        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var projected = await QueryAsync(ctx, instance.Id.ToString());

        // Mirror the production full-build path: aggregate loaded with the active-only filtered include,
        // full correlation set read separately.
        var loaded = await ctx.Instances
            .AsNoTracking()
            .Include(i => i.DataList)
            .Include(i => i.ChildCorrelations.Where(c => !c.IsCompleted))
            .FirstAsync(i => i.Id == instance.Id);
        var allCorrelations = await ctx.Set<InstanceCorrelation>()
            .AsNoTracking()
            .Where(c => c.ParentInstanceId == instance.Id)
            .ToListAsync();

        var fromInstance = InstanceStateFingerprint.FromInstance(loaded, allCorrelations);

        projected.ShouldNotBeNull();
        fromInstance.ShouldBe(projected);
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

    /// <summary>
    /// The compiled poll-path queries must produce the exact same fingerprint as the uncompiled
    /// reference projection — a diverging member invalidates every outstanding ETag fleet-wide.
    /// </summary>
    [Fact]
    public async Task CompiledQueries_MatchUncompiledReference()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "fp-compiled-parity");
        instance.SetEffectiveState("review");

        var completed = CreateCorrelation(instance.Id, subFlowType: "S");
        completed.ApplyTerminalOutcome(
            SubItemTerminalOutcome.Completed, new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        instance.AddCorrelation(completed);
        instance.AddCorrelation(CreateCorrelation(instance.Id, subFlowType: "S"));

        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var compiled = EfCoreInstanceRepository.CompiledFingerprintQueries.For(ctx);
        var reference = await QueryAsync(ctx, instance.Id.ToString());

        reference.ShouldNotBeNull();
        (await compiled.StateById(ctx, instance.Id, CancellationToken.None)).ShouldBe(reference);
        (await compiled.StateByKey(ctx, "fp-compiled-parity", CancellationToken.None)).ShouldBe(reference);
        (await compiled.StateById(ctx, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();
    }

    /// <summary>
    /// A compiled query binds to the model of the first context it runs against, and
    /// schema-per-flow bakes the schema name into each compiled model. The per-model cache in
    /// <see cref="EfCoreInstanceRepository.CompiledFingerprintQueries"/> must therefore hand each
    /// schema its own delegates — one shared delegate would silently serve every tenant from the
    /// first tenant's schema.
    /// </summary>
    [Fact]
    public async Task CompiledQueries_AreScopedPerSchemaModel()
    {
        var tenantInstance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "fp-tenant-only");
        tenantInstance.SetEffectiveState("review");

        await using (var setupCtx = CreateContext("tenant_fp"))
        {
            await setupCtx.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS tenant_fp");
            var creator = setupCtx.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
            await creator.CreateTablesAsync();

            setupCtx.Instances.Add(tenantInstance);
            await setupCtx.SaveChangesAsync();
        }

        await using var tenantCtx = CreateContext("tenant_fp");
        await using var publicCtx = CreateContext();

        var tenantCompiled = EfCoreInstanceRepository.CompiledFingerprintQueries.For(tenantCtx);
        var publicCompiled = EfCoreInstanceRepository.CompiledFingerprintQueries.For(publicCtx);

        tenantCompiled.ShouldNotBeSameAs(publicCompiled);

        var fromTenant = await tenantCompiled.StateById(tenantCtx, tenantInstance.Id, CancellationToken.None);
        fromTenant.ShouldNotBeNull();
        fromTenant!.Key.ShouldBe("fp-tenant-only");

        // The same id through the public-schema delegates must not see the tenant's row.
        (await publicCompiled.StateById(publicCtx, tenantInstance.Id, CancellationToken.None)).ShouldBeNull();
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

    private WorkflowDbContext CreateContext(string schema = "public")
    {
        // Mirror production model caching: SchemaAwareModelCacheKeyFactory compiles one model per
        // schema. Without it EF's default cache (keyed by context type alone) would hand the
        // second schema the first schema's model.
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>()
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema(schema));
    }
}
