using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
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
/// Incidents live in their own <c>InstanceIncidents</c> table and are never included with the
/// aggregate. These tests pin, against a real PostgreSQL:
/// <list type="bullet">
///   <item>a tracked aggregate persists new incidents through the navigation and raises the flag;</item>
///   <item>a <b>detached</b> aggregate updated through <see cref="EfCoreInstanceRepository.UpdateAsync"/>
///   still INSERTs its pending incidents (Aether's <c>Set.Update(graph)</c> would otherwise mark the
///   client-keyed child Modified and fail with a 0-row UPDATE);</item>
///   <item><see cref="EfCoreInstanceRepository.LoadActiveIncidentsAsync"/> on tracked and no-tracking roots;</item>
///   <item>the <see cref="EfCoreInstanceIncidentRepository"/> read APIs and cascade delete.</item>
/// </list>
/// </summary>
public sealed class InstanceIncidentPersistenceTests : IAsyncLifetime
{
    private const string Flow = "incident-persistence-flow";

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
    public async Task TrackedAggregate_AddIncident_InsertsRowAndPersistsFlag()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "tracked-add");
        await SeedAsync(instance);

        await using (var ctx = CreateContext())
        {
            var repository = CreateRepository(ctx);
            var tracked = await ctx.Instances.FirstAsync(i => i.Id == instance.Id);
            tracked.AddIncident(CreateIncident("Task:Http:503"));

            await repository.UpdateAsync(tracked, autoSave: false);
            await ctx.SaveChangesAsync();
            tracked.GetPendingIncidents().ShouldBeEmpty();
        }

        await using var verify = CreateContext();
        var rows = await verify.InstanceIncidents.AsNoTracking().Where(i => i.InstanceId == instance.Id).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].ErrorCode.ShouldBe("Task:Http:503");
        rows[0].IsResolved.ShouldBeFalse();
        (await verify.Instances.AsNoTracking().Where(i => i.Id == instance.Id).Select(i => i.HasActiveIncident).SingleAsync())
            .ShouldBeTrue();
    }

    [Fact]
    public async Task DetachedAggregate_UpdateAsync_InsertsPendingIncidentInsteadOfFailing()
    {
        // Mirrors InstanceRetryAppService / MarkInstanceFaultedAsync: the aggregate was loaded in one
        // scope and is updated through a fresh DbContext (RequiresNew UoW).
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "detached-add");
        await SeedAsync(instance);

        Instance detached;
        await using (var loadCtx = CreateContext())
        {
            detached = await loadCtx.Instances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        }

        detached.AddIncident(CreateIncident("Pipeline:Unhandled"));

        await using (var updateCtx = CreateContext())
        {
            var repository = CreateRepository(updateCtx);
            await repository.UpdateAsync(detached, autoSave: false);
            await updateCtx.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var rows = await verify.InstanceIncidents.AsNoTracking().Where(i => i.InstanceId == instance.Id).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].ErrorCode.ShouldBe("Pipeline:Unhandled");
    }

    [Fact]
    public async Task DetachedAggregate_WithoutSafetyNet_WouldFail_DocumentsTheHazard()
    {
        // Pins the reason the safety net exists: plain Set.Update on a detached root marks a new
        // client-keyed child Modified → 0 rows affected → concurrency exception.
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "detached-hazard");
        await SeedAsync(instance);

        Instance detached;
        await using (var loadCtx = CreateContext())
            detached = await loadCtx.Instances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);

        detached.AddIncident(CreateIncident("hazard"));

        await using var ctx = CreateContext();
        ctx.Instances.Update(detached);
        // AetherDbContext translates EF's DbUpdateConcurrencyException into its own type.
        var ex = await Should.ThrowAsync<Exception>(() => ctx.SaveChangesAsync());
        ex.GetType().Name.ShouldContain("Concurrency");
    }

    [Fact]
    public async Task LoadActiveIncidentsAsync_TrackedRoot_LoadsUnresolvedOnlyAndResolvePersists()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "load-tracked");
        var resolved = CreateIncident("resolved");
        resolved.Resolve();
        instance.AddIncident(resolved);
        instance.AddIncident(CreateIncident("active"));
        await SeedAsync(instance);

        await using (var ctx = CreateContext())
        {
            var repository = CreateRepository(ctx);
            var tracked = await ctx.Instances.FirstAsync(i => i.Id == instance.Id);
            tracked.HasActiveIncident.ShouldBeTrue();
            tracked.GetLoadedIncidents().ShouldBeEmpty("incidents are never included by default");

            await repository.LoadActiveIncidentsAsync(tracked);

            tracked.IncidentsLoaded.ShouldBeTrue();
            tracked.GetLoadedIncidents().Select(i => i.ErrorCode).ShouldBe(["active"]);

            // In-memory only, and that is enough here: the rows came in through THIS context's
            // change tracker (the shape FinalizeTransitionStep relies on), so SaveChanges writes them.
            tracked.ResolveOpenIncidents().ShouldNotBeEmpty();
            tracked.HasActiveIncident.ShouldBeFalse();
            await ctx.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        (await verify.InstanceIncidents.AsNoTracking().CountAsync(i => i.InstanceId == instance.Id && !i.IsResolved)).ShouldBe(0);
        (await verify.Instances.AsNoTracking().Where(i => i.Id == instance.Id).Select(i => i.HasActiveIncident).SingleAsync())
            .ShouldBeFalse();
    }

    [Fact]
    public async Task LoadActiveIncidentsAsync_NoTrackingRoot_AcceptsRows()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "load-notracking");
        instance.AddIncident(CreateIncident("active"));
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx);
        var detached = await ctx.Instances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);

        await repository.LoadActiveIncidentsAsync(detached);

        detached.IncidentsLoaded.ShouldBeTrue();
        detached.GetLoadedIncidents().Single().ErrorCode.ShouldBe("active");
        ctx.ChangeTracker.Entries().ShouldBeEmpty("a no-tracking read must not start tracking anything");
    }

    [Fact]
    public async Task LoadActiveIncidentsAsync_FlagFalse_IssuesNoQueryButMarksLoaded()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "load-none");
        await SeedAsync(instance);

        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx);
        var tracked = await ctx.Instances.FirstAsync(i => i.Id == instance.Id);

        await repository.LoadActiveIncidentsAsync(tracked);

        tracked.IncidentsLoaded.ShouldBeTrue();
        tracked.ResolveOpenIncidents().ShouldBeEmpty();
    }

    [Fact]
    public async Task IncidentsLoadedNoTracking_AreNotReInsertedByAnotherContextTrackingTheSameAggregate()
    {
        // REGRESSION. The retry path loads the aggregate in the ambient request scope and loads its
        // incidents inside a RequiresNew scope. When the no-tracking rows were attached to the EF
        // navigation, the ambient context discovered them as new children of a tracked root at its
        // own commit and INSERTed them again: 23505 on PK_InstanceIncidents, with the response body
        // already half-written. Loaded rows therefore live off the navigation.
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "detached-load");
        instance.AddIncident(CreateIncident("original"));
        await SeedAsync(instance);

        await using var ambient = CreateContext();
        var tracked = await ambient.Instances.FirstAsync(i => i.Id == instance.Id);

        // A different context loads the incidents onto that very aggregate, the way an inner scope does.
        await using (var inner = CreateContext())
        {
            await CreateRepository(inner).LoadActiveIncidentsAsync(tracked);
        }

        tracked.GetLoadedIncidents().Select(i => i.ErrorCode).ShouldBe(["original"]);

        // The ambient context must have nothing to insert.
        ambient.ChangeTracker.DetectChanges();
        ambient.ChangeTracker.Entries<InstanceIncident>()
            .Select(e => e.State)
            .ShouldNotContain(EntityState.Added);

        await Should.NotThrowAsync(() => ambient.SaveChangesAsync());

        await using var verify = CreateContext();
        (await verify.InstanceIncidents.CountAsync(i => i.InstanceId == instance.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAllAsync_ClosesEveryOpenRowOfOneInstanceOnly()
    {
        // The companion to the test above: with the rows off the navigation, an in-memory Resolve()
        // has nothing behind it, so the retry path persists the resolutions explicitly — and it
        // resolves the whole open set, because resolving only the newest left a recovered instance
        // reporting an active incident.
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "detached-resolve");
        var alreadyResolved = CreateIncident("already-resolved");
        alreadyResolved.Resolve();
        instance.AddIncident(alreadyResolved);
        instance.AddIncident(CreateIncident("open-1"));
        instance.AddIncident(CreateIncident("open-2"));
        await SeedAsync(instance);

        var other = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "detached-resolve-other");
        other.AddIncident(CreateIncident("other-open"));
        await SeedAsync(other);

        await using var ctx = CreateContext();
        var repository = new EfCoreInstanceIncidentRepository(
            new FixedDbContextProvider(ctx), new ServiceCollection().BuildServiceProvider());

        (await repository.ResolveAllAsync(instance.Id, DateTime.UtcNow)).ShouldBe(2);
        // Idempotent: the predicate already excludes resolved rows.
        (await repository.ResolveAllAsync(instance.Id, DateTime.UtcNow)).ShouldBe(0);

        await using var verify = CreateContext();
        var stored = await verify.InstanceIncidents.AsNoTracking()
            .Where(i => i.InstanceId == instance.Id).ToListAsync();
        stored.Count.ShouldBe(3);
        stored.ShouldAllBe(i => i.IsResolved && i.ResolvedAt != null);

        (await verify.InstanceIncidents.AsNoTracking()
            .CountAsync(i => i.InstanceId == other.Id && !i.IsResolved))
            .ShouldBe(1, "another instance's incidents are not touched");
    }

    [Fact]
    public async Task TryUnfaultAsync_FlipsFaultedToActiveAndLeavesNothingForAnAmbientCommitToOverwrite()
    {
        // REGRESSION. The retry request used to load the aggregate TRACKED in the ambient request
        // unit of work; Unfault() set Active on that very object, the pipeline faulted a RELOADED
        // aggregate in its own scope, and the ambient commit at the end of the request then wrote
        // its stale Active over the persisted Faulted. The instance was left Active, parked, with an
        // open incident — and `retry` refused it from then on because it was no longer faulted.
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "retry-overwrite");
        instance.AddIncident(CreateIncident("boundary"));
        await SeedAsync(instance);

        await using (var seed = CreateContext())
        {
            await seed.Instances.Where(i => i.Id == instance.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, InstanceStatus.Faulted));
        }

        // The ambient request scope: read-only, so it tracks nothing.
        await using var ambient = CreateContext();
        var loaded = await ambient.Instances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);

        await using (var inner = CreateContext())
        {
            var repository = CreateRepository(inner);
            await repository.LoadActiveIncidentsAsync(loaded);
            (await repository.TryUnfaultAsync(loaded)).ShouldBeTrue();

            var incidents = new EfCoreInstanceIncidentRepository(
                new FixedDbContextProvider(inner), new ServiceCollection().BuildServiceProvider());
            (await incidents.ResolveAllAsync(loaded.Id, DateTime.UtcNow)).ShouldBe(1);
        }

        // The CAS is a lost race the second time: nothing to unfault any more.
        await using (var again = CreateContext())
        {
            (await CreateRepository(again).TryUnfaultAsync(loaded)).ShouldBeFalse();
        }

        // The retried transition faults again, in its own scope on its own aggregate.
        await using (var pipeline = CreateContext())
        {
            var reloaded = await pipeline.Instances.FirstAsync(i => i.Id == instance.Id);
            reloaded.Status.ShouldBe(InstanceStatus.Active, "the unfault must be committed and visible");
            reloaded.AddIncident(CreateIncident("second-failure"));
            reloaded.Fault("test-domain");
            await CreateRepository(pipeline).UpdateAsync(reloaded, autoSave: true);
        }

        ambient.ChangeTracker.Entries<Instance>().ShouldBeEmpty("a no-tracking load must track nothing");
        await Should.NotThrowAsync(() => ambient.SaveChangesAsync());

        await using var verify = CreateContext();
        var final = await verify.Instances.AsNoTracking().SingleAsync(i => i.Id == instance.Id);
        final.Status.ShouldBe(InstanceStatus.Faulted, "the ambient commit overwrote the fault again");
        final.HasActiveIncident.ShouldBeTrue();
        (await verify.InstanceIncidents.AsNoTracking()
            .CountAsync(i => i.InstanceId == instance.Id && !i.IsResolved)).ShouldBe(1);
        (await verify.InstanceIncidents.AsNoTracking()
            .CountAsync(i => i.InstanceId == instance.Id)).ShouldBe(2, "history is unbounded");
    }

    [Fact]
    public async Task IncidentRepository_PagesNewestFirstAndPicksTheNewestOpenRow()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "repo-reads");
        var other = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "repo-reads-other");
        for (var i = 0; i < 7; i++)
        {
            var incident = CreateIncident($"code-{i}", createdAt: new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc));
            if (i < 5) incident.Resolve();
            instance.AddIncident(incident);
        }
        await SeedAsync(instance);
        await SeedAsync(other);

        await using var ctx = CreateContext();
        var repository = new EfCoreInstanceIncidentRepository(
            new FixedDbContextProvider(ctx), new ServiceCollection().BuildServiceProvider());

        var page1 = await repository.GetHistoryPagedAsync(instance.Id, page: 1, pageSize: 3);
        page1.Items.Select(i => i.ErrorCode).ShouldBe(["code-6", "code-5", "code-4"]);
        page1.HasNext.ShouldBeTrue();

        var page3 = await repository.GetHistoryPagedAsync(instance.Id, page: 3, pageSize: 3);
        page3.Items.Select(i => i.ErrorCode).ShouldBe(["code-0"]);
        page3.HasNext.ShouldBeFalse();

        // The active-incident endpoint's read: newest UNRESOLVED, ignoring the five resolved rows.
        var active = await repository.GetActiveAsync(instance.Id);
        active.ShouldNotBeNull();
        active!.ErrorCode.ShouldBe("code-6", "newest unresolved wins");

        // An instance with no incident at all answers null rather than throwing — that is the 404.
        (await repository.GetActiveAsync(other.Id)).ShouldBeNull();

        // And once everything is closed, so does an instance that used to carry one.
        await repository.ResolveAllAsync(instance.Id, DateTime.UtcNow);
        (await repository.GetActiveAsync(instance.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task DeletingInstance_CascadesIncidents()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "cascade");
        instance.AddIncident(CreateIncident("x"));
        await SeedAsync(instance);

        await using (var ctx = CreateContext())
        {
            await ctx.Instances.Where(i => i.Id == instance.Id).ExecuteDeleteAsync();
        }

        await using var verify = CreateContext();
        (await verify.InstanceIncidents.CountAsync(i => i.InstanceId == instance.Id)).ShouldBe(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static InstanceIncident CreateIncident(string errorCode, DateTime? createdAt = null)
    {
        var incident = InstanceIncidentFactory.Create(
            state: "review", transition: "submit", taskKey: "call-service",
            message: "boom", errorCode: errorCode, errorLayer: "Task");
        if (createdAt is not null)
        {
            // CreatedAt is init-only; rebuild with the requested timestamp for deterministic ordering.
            incident = new InstanceIncident(incident.Id)
            {
                CreatedAt = createdAt.Value,
                State = incident.State,
                Transition = incident.Transition,
                Task = incident.Task,
                Message = incident.Message,
                ErrorCode = incident.ErrorCode,
                ErrorLayer = incident.ErrorLayer,
                TraceId = incident.TraceId
            };
        }
        return incident;
    }

    private async Task SeedAsync(Instance instance)
    {
        await using var ctx = CreateContext();
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();
        instance.ClearPendingIncidents();
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
