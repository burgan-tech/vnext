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
/// Integration tests for the execution-snapshot projection
/// (<see cref="EfCoreInstanceRepository.QueryExecutionSnapshotAsync"/>) against a real PostgreSQL:
/// the active blocking SubFlow reference must translate (correlated FirstOrDefault over the
/// SubFlowType value-converted column) and must pick exactly what <c>Instance.Subflow</c> picks.
/// </summary>
public sealed class InstanceExecutionSnapshotQueryTests : IAsyncLifetime
{
    private const string Flow = "snapshot-test-flow";
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
    public async Task OpenSubFlowCorrelation_ProjectsActiveSubFlowRef()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "snap-active-s");
        var childId = Guid.NewGuid();
        instance.AddCorrelation(CreateCorrelation(instance.Id, "S", childId, "child-domain", "child-flow", "2.1.0"));
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var snapshot = await QueryAsync(ctx, instance.Id.ToString());

        snapshot.ShouldNotBeNull();
        snapshot!.HasActiveSubFlow.ShouldBeTrue();
        snapshot.ActiveSubFlow.ShouldBe(new ActiveSubFlowRef(childId, "child-domain", "child-flow", "2.1.0"));
    }

    [Fact]
    public async Task OnlySubProcessOrCompletedCorrelations_ProjectsNullActiveSubFlow()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "snap-no-active-s");
        var completed = CreateCorrelation(instance.Id, "S", Guid.NewGuid(), "child-domain", "child-flow", "1.0.0");
        completed.Completed();
        instance.AddCorrelation(completed);
        instance.AddCorrelation(CreateCorrelation(instance.Id, "P", Guid.NewGuid(), "child-domain", "proc-flow", "1.0.0"));
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var snapshot = await QueryAsync(ctx, instance.Id.ToString());

        snapshot.ShouldNotBeNull();
        snapshot!.HasActiveSubFlow.ShouldBeFalse();
        snapshot.ActiveSubFlow.ShouldBeNull();
    }

    [Fact]
    public async Task NoCorrelations_ProjectsNullActiveSubFlow_ByKeyToo()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "snap-plain");
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var snapshot = await QueryAsync(ctx, "snap-plain");

        snapshot.ShouldNotBeNull();
        snapshot!.Id.ShouldBe(instance.Id);
        snapshot.ActiveSubFlow.ShouldBeNull();
    }

    [Fact]
    public async Task ProjectsTheRawEffectiveStatusColumn()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "snap-effective");
        instance.AddCorrelation(CreateCorrelation(instance.Id, "S", Guid.NewGuid(), "d", "f", "1.0.0"));
        instance.SetEffectiveStatus(InstanceStatus.Busy);
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var snapshot = await QueryAsync(ctx, instance.Id.ToString());

        snapshot!.EffectiveStatus.ShouldBe(InstanceStatus.Busy);
    }

    [Fact]
    public async Task CompareAndSetEffectiveStatus_WritesOnlyWhileTheColumnHoldsTheExpectedValue()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, FlowVersion, "snap-cas");
        instance.SetEffectiveStatus(InstanceStatus.Busy);
        await SeedAsync(instance);

        // Matches: Busy -> Active.
        await using (var ctx = CreateContext())
        {
            (await EfCoreInstanceRepository.CompareAndSetEffectiveStatusAsync(
                    ctx.Instances, instance.Id, InstanceStatus.Busy, InstanceStatus.Active, CancellationToken.None))
                .ShouldBeTrue();
        }

        // The column has moved (now Active): a second revert expecting Busy is a no-op.
        await using (var ctx = CreateContext())
        {
            (await EfCoreInstanceRepository.CompareAndSetEffectiveStatusAsync(
                    ctx.Instances, instance.Id, InstanceStatus.Busy, InstanceStatus.Completed, CancellationToken.None))
                .ShouldBeFalse();
        }

        await using var read = CreateContext();
        var snapshot = await QueryAsync(read, instance.Id.ToString());
        snapshot!.EffectiveStatus.ShouldBe(InstanceStatus.Active);
    }

    private static Task<InstanceExecutionSnapshot?> QueryAsync(WorkflowDbContext ctx, string identifier) =>
        EfCoreInstanceRepository.QueryExecutionSnapshotAsync(
            ctx.Instances.AsNoTracking(), identifier, CancellationToken.None);

    private static InstanceCorrelation CreateCorrelation(
        Guid instanceId, string subFlowType, Guid childId, string domain, string flow, string version) =>
        InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: instanceId,
            parentState: "review",
            subFlowInstanceId: childId,
            subFlowType: subFlowType,
            subFlowDomain: domain,
            subFlowName: flow,
            subFlowVersion: version);

    private async Task SeedAsync(Instance instance)
    {
        await using var ctx = CreateContext();
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();
    }

    private WorkflowDbContext CreateContext(string schema = "public")
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>()
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema(schema));
    }
}
