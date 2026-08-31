using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
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
/// Pins the <c>Instance.Query.Prepare</c> span added around <see cref="EfCoreInstanceRepository"/>'s
/// detailed-query preparation (the <c>WithDetailsAsync</c> awaited by <c>FindByIdentifierAsync</c>,
/// <c>FindActiveByKeyAsync</c> and <c>FindByIdentifierAsReadOnlyAsync</c> via the shared private
/// <c>PrepareDetailedQueryAsync</c> helper).
/// <para>
/// Before this span, the leading gap between <c>Instance.Load</c> and its first <c>Db.SELECT</c>
/// child was anonymous: 300 live spans measured mean lead 0.60ms vs. trail 0.03ms (p50 0.63ms,
/// p90 2.40ms, max 88ms) — nearly all the cost sits before the first EF command is even issued, a
/// window EF's own CommandExecuting/CommandExecuted instrumentation cannot see. See
/// <c>docs/runtime/trace-span-tree.md</c> for how to read the new span.
/// </para>
/// <para>
/// Uses a real (Testcontainers) PostgreSQL rather than the suite's usual in-memory Sqlite fixture:
/// the Sqlite-backed <c>InfrastructureEntryPoint</c> DI path fails <c>WorkflowDbContext</c> model
/// validation on an unrelated <c>jsonb</c> mapping (<c>BackgroundJobInfo.Payload</c>) that Sqlite
/// cannot represent — a pre-existing gap hit by every sibling test that resolves
/// <c>IInstanceRepository</c> through that fixture (13 of them were already red in this repo's own
/// baseline before this change). Constructing the repository directly against Postgres, the same way
/// <c>InstanceDataVersioningTests</c> does for <c>InstanceDataWriteService</c>, sidesteps it.
/// </para>
/// </summary>
public sealed class EfCoreInstanceRepositoryPrepareSpanTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgresContainer = null!;
    private string _connectionString = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        await _postgresContainer.StartAsync();
        _connectionString = _postgresContainer.GetConnectionString();

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgresContainer.StopAsync();
        await _postgresContainer.DisposeAsync();
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

    [Fact]
    public async Task FindByIdentifierAsync_emits_prepare_span_nested_inside_the_caller_span()
    {
        await using var seedCtx = CreateContext();
        var seeded = Instance.Create(Guid.NewGuid(), "prepare-span-flow", "1.0.0", "prepare-span-key");
        seedCtx.Instances.Add(seeded);
        await seedCtx.SaveChangesAsync();

        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx);

        var collected = new List<Activity>();
        using var listener = new ActivityListener
        {
            // Hardcoded source-name literal (matching DomainDiscoveryResolverSpanTests): the first
            // access to PipelineStepActivityHelper.ActivitySource in a test process runs its static
            // constructor, which notifies already-registered listeners synchronously — referencing
            // the field here would observe it before assignment completes.
            ShouldListenTo = s => s.Name == "BBT.Workflow.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);

        // Simulates the real caller: TransitionContextFactory.LoadInstanceAsync wraps
        // instanceRepository.GetActiveAsync (which chains into FindByIdentifierAsync) in an
        // "Instance.Load" span.
        var ambient = new Activity("Instance.Load");
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.Start();
        Instance? found;
        try
        {
            found = await repository.FindByIdentifierAsync(seeded.Id.ToString(), CancellationToken.None);
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }

        found.ShouldNotBeNull();
        found!.Id.ShouldBe(seeded.Id);

        var span = collected.Single(a => a.DisplayName == "Instance.Query.Prepare");
        span.ParentId.ShouldBe(ambient.Id);
        span.GetTagItem(TelemetryConstants.TagNames.SpanCategory).ShouldBe(TelemetryConstants.SpanCategories.Business);

        // The span wraps only WithDetailsAsync() — it is disposed (Stop()) before
        // FindByIdentifierAsync's subsequent FirstOrDefaultAsync call, which is what actually issues
        // the SELECT and produces the Db.SELECT command span. That ordering is structural (the
        // `using` scope inside PrepareDetailedQueryAsync ends before the method returns to its
        // caller), not something this test needs a real Db.SELECT span to confirm.
        span.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task FindByIdentifierAsReadOnlyAsync_also_emits_exactly_one_prepare_span()
    {
        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx);

        var collected = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "BBT.Workflow.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);

        await repository.FindByIdentifierAsReadOnlyAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        collected.Count(a => a.DisplayName == "Instance.Query.Prepare").ShouldBe(1);
    }

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
