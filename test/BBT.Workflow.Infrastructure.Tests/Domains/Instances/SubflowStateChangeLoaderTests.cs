using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Pins <see cref="EfCoreInstanceRepository.FindForSubflowStateChangeAsync"/> against a real
/// PostgreSQL. It is the load behind the runtime's highest-volume subflow signal, and it is narrower
/// than every other loader on purpose: tracked parent, ONLY the open correlation of the child that
/// changed state, and NO <see cref="Instance.DataList"/>. The default detail load
/// (<c>WithDetailsAsync</c>) pulls the entire instance-data history unsplit, which this path never
/// reads.
/// <para>
/// The filtered include carries a compound predicate (<c>SubFlowInstanceId == x &amp;&amp;
/// !IsCompleted</c>); these tests exist because that has to translate and filter correctly in the
/// database, not just compile.
/// </para>
/// </summary>
public sealed class SubflowStateChangeLoaderTests : IAsyncLifetime
{
    private const string Flow = "substate-loader-flow";

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
    public async Task Loads_Only_The_Target_Correlation_And_No_Data()
    {
        var parent = CreateParent(out var targetSubId, out var siblingSubId);
        await SeedAsync(parent);

        await using var ctx = CreateContext();
        var loaded = await CreateRepository(ctx)
            .FindForSubflowStateChangeAsync(parent.Id, targetSubId, CancellationToken.None);

        loaded.ShouldNotBeNull();
        loaded!.ChildCorrelations.Count.ShouldBe(1);
        loaded.ChildCorrelations.Single().SubFlowInstanceId.ShouldBe(targetSubId);
        loaded.ChildCorrelations.ShouldNotContain(c => c.SubFlowInstanceId == siblingSubId);

        // The whole point of the narrow loader: the data history is not on the wire.
        loaded.DataList.ShouldBeEmpty();
    }

    /// <summary>
    /// A correlation that closed between publish and delivery must not come back — the terminal path
    /// owns the parent's effective state from that point on, and the service's
    /// <c>correlation_not_found</c> branch depends on this filter, exactly as the default detail load
    /// does.
    /// </summary>
    [Fact]
    public async Task Excludes_A_Completed_Correlation()
    {
        var parent = CreateParent(out var targetSubId, out _);
        parent.FindCorrelationBySubInstanceId(targetSubId)!.Completed();
        await SeedAsync(parent);

        await using var ctx = CreateContext();
        var loaded = await CreateRepository(ctx)
            .FindForSubflowStateChangeAsync(parent.Id, targetSubId, CancellationToken.None);

        loaded.ShouldNotBeNull();
        loaded!.FindCorrelationBySubInstanceId(targetSubId).ShouldBeNull();
    }

    /// <summary>
    /// The aggregate must come back CHANGE-TRACKED: the service mutates the correlation and the
    /// parent in memory and relies on one <c>UpdateAsync(autoSave)</c> to write both in a single
    /// batch — the same P → C write order every terminal path uses.
    /// </summary>
    [Fact]
    public async Task Returns_A_Tracked_Aggregate_Whose_Mutations_Persist()
    {
        var parent = CreateParent(out var targetSubId, out _);
        await SeedAsync(parent);
        var changedAt = DateTime.UtcNow;

        await using (var ctx = CreateContext())
        {
            var loaded = await CreateRepository(ctx)
                .FindForSubflowStateChangeAsync(parent.Id, targetSubId, CancellationToken.None);

            loaded!.FindCorrelationBySubInstanceId(targetSubId)!.UpdateSubFlowState("child-running", changedAt);
            loaded.PropagateEffectiveStateToParent("child-running", StateType.Intermediate, StateSubType.None);
            await CreateRepository(ctx).UpdateAsync(loaded, true, CancellationToken.None);
        }

        await using (var verify = CreateContext())
        {
            var reloaded = await verify.Instances
                .Include(i => i.ChildCorrelations)
                .SingleAsync(i => i.Id == parent.Id);

            reloaded.GetEffectiveState.ShouldBe("child-running");
            var correlation = reloaded.ChildCorrelations.Single(c => c.SubFlowInstanceId == targetSubId);
            correlation.SubFlowCurrentState.ShouldBe("child-running");
            correlation.SubFlowStateChangedAt.ShouldNotBeNull();
        }
    }

    private static Instance CreateParent(out Guid targetSubId, out Guid siblingSubId)
    {
        targetSubId = Guid.NewGuid();
        siblingSubId = Guid.NewGuid();
        var parent = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", $"parent-{Guid.NewGuid():N}");
        parent.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        parent.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), parent.Id, "waiting-child", targetSubId,
            SubFlowType.SubFlow.Code, "bank", "child-flow", "1.0.0"));
        parent.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), parent.Id, "waiting-child", siblingSubId,
            SubFlowType.SubFlow.Code, "bank", "sibling-flow", "1.0.0"));
        return parent;
    }

    private async Task SeedAsync(Instance instance)
    {
        await using var ctx = CreateContext();
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();
    }

    private WorkflowDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema("public"));
    }

    private static EfCoreInstanceRepository CreateRepository(WorkflowDbContext context) => new(
        new FixedDbContextProvider(context),
        new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IRuntimeInfoProvider>(),
        Substitute.For<IDataSinkManager>(),
        new StaticCurrentSchema("public"),
        Substitute.For<ISchemaValidator>(),
        Options.Create(new WorkflowExecutionOptions()),
        NullLogger<EfCoreInstanceRepository>.Instance);

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
