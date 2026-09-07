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
/// Pins the <c>Instance.Query.Prepare</c> and <c>Instance.Query.Execute</c> spans around
/// <see cref="EfCoreInstanceRepository"/>'s detailed-query preparation and materialization.
/// <para>
/// <c>Prepare</c> isolates query construction; <c>Execute</c> owns EF query compilation, connection
/// acquisition, command spans and materialization. EF's own command instrumentation cannot see the
/// work before the first command is issued. See <c>docs/runtime/trace-span-tree.md</c>.
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
    public async Task FindByIdentifierAsync_emits_adjacent_prepare_and_execute_spans_inside_the_caller()
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

        var prepare = collected.Single(a => a.DisplayName == "Instance.Query.Prepare");
        var execute = collected.Single(a => a.DisplayName == "Instance.Query.Execute");
        prepare.ParentId.ShouldBe(ambient.Id);
        execute.ParentId.ShouldBe(ambient.Id);
        prepare.GetTagItem(TelemetryConstants.TagNames.SpanCategory)
            .ShouldBe(TelemetryConstants.SpanCategories.Business);
        execute.GetTagItem(TelemetryConstants.TagNames.SpanCategory)
            .ShouldBe(TelemetryConstants.SpanCategories.Business);
        execute.StartTimeUtc.ShouldBeGreaterThanOrEqualTo(prepare.StartTimeUtc + prepare.Duration);
    }

    [Fact]
    public async Task FindByIdentifierAsReadOnlyAsync_also_emits_exactly_one_span_for_each_phase()
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
        collected.Count(a => a.DisplayName == "Instance.Query.Execute").ShouldBe(1);
    }

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
