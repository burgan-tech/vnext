using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Focused tests for the atomic delivery-state operations implemented by
/// <see cref="EfCoreInstanceJobRepository"/>.
/// </summary>
public sealed class EfCoreInstanceJobRepositoryTests
{
    [Fact]
    public async Task TryClaimAsync_WhenLeaseIsHeld_ShouldAllowExactlyOneClaim()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var job = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "approve");
        await fixture.InsertAsync(job);
        var firstToken = Guid.NewGuid();

        var firstClaim = await fixture.Repository.TryClaimAsync(
            job.JobId, firstToken, TimeSpan.FromMinutes(5));
        var competingClaim = await fixture.Repository.TryClaimAsync(
            job.JobId, Guid.NewGuid(), TimeSpan.FromMinutes(5));

        firstClaim.ShouldBeTrue();
        competingClaim.ShouldBeFalse();

        var persisted = await fixture.FindAsync(job.JobId);
        persisted.ShouldNotBeNull();
        persisted.DispatchStatus.ShouldBe(InstanceJobDispatchStatus.Processing);
        persisted.AttemptCount.ShouldBe(1);
        persisted.ProcessingAt.ShouldNotBeNull();
        persisted.ProcessingLeaseUntil.ShouldNotBeNull();
        persisted.ProcessingToken.ShouldBe(firstToken);
        persisted.ProcessingLeaseUntil!.Value.ShouldBeGreaterThan(persisted.ProcessingAt!.Value);
    }

    [Fact]
    public async Task TryClaimAsync_WhenLeaseExpired_ShouldPermitRecoveryAndIncrementAttempt()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var job = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "approve");
        await fixture.InsertAsync(job);

        var firstToken = Guid.NewGuid();
        var recoveredToken = Guid.NewGuid();
        (await fixture.Repository.TryClaimAsync(
            job.JobId, firstToken, TimeSpan.FromMinutes(5))).ShouldBeTrue();

        var expiredAt = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Context.InstanceJobs
            .Where(item => item.JobId == job.JobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingLeaseUntil, expiredAt));

        (await fixture.Repository.TryClaimAsync(
            job.JobId, recoveredToken, TimeSpan.FromMinutes(5))).ShouldBeTrue();

        var persisted = await fixture.FindAsync(job.JobId);
        persisted.ShouldNotBeNull();
        persisted.DispatchStatus.ShouldBe(InstanceJobDispatchStatus.Processing);
        persisted.AttemptCount.ShouldBe(2);
        persisted.ProcessingToken.ShouldBe(recoveredToken);
        persisted.ProcessingLeaseUntil!.Value.ShouldBeGreaterThan(DateTime.UtcNow);
    }

    [Fact]
    public async Task TryClaimAsync_WhenJobIsInactiveOrUnknown_ShouldNotClaim()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var job = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "approve");
        job.MarkAsProcessed();
        await fixture.InsertAsync(job);

        (await fixture.Repository.TryClaimAsync(
            job.JobId, Guid.NewGuid(), TimeSpan.FromMinutes(5))).ShouldBeFalse();
        (await fixture.Repository.TryClaimAsync(
            Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(5))).ShouldBeFalse();
    }

    [Fact]
    public async Task MarkAsProcessedByJobIdAsync_ShouldOnlyCompleteTheMatchingActiveJob()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var instanceId = Guid.NewGuid();
        var target = CreateAdmissionJob(instanceId, Guid.NewGuid(), "approve");
        var other = CreateAdmissionJob(instanceId, Guid.NewGuid(), "reject");
        await fixture.InsertAsync(target, other);
        var processingToken = Guid.NewGuid();
        (await fixture.Repository.TryClaimAsync(
            target.JobId, processingToken, TimeSpan.FromMinutes(5))).ShouldBeTrue();

        (await fixture.Repository.MarkAsProcessedByJobIdAsync(
            target.JobId, Guid.NewGuid())).ShouldBeFalse();

        (await fixture.Repository.MarkAsProcessedByJobIdAsync(
            target.JobId, processingToken)).ShouldBeTrue();

        var persistedTarget = await fixture.FindAsync(target.JobId);
        persistedTarget.ShouldNotBeNull();
        persistedTarget.IsActive.ShouldBeFalse();
        persistedTarget.DispatchStatus.ShouldBe(InstanceJobDispatchStatus.Completed);
        persistedTarget.Payload.ShouldBeNull();

        var persistedOther = await fixture.FindAsync(other.JobId);
        persistedOther.ShouldNotBeNull();
        persistedOther.IsActive.ShouldBeTrue();
        persistedOther.DispatchStatus.ShouldBe(InstanceJobDispatchStatus.PendingDispatch);
        persistedOther.Payload.ShouldNotBeNull();

        (await fixture.Repository.MarkAsProcessedByJobIdAsync(
            Guid.NewGuid(), Guid.NewGuid())).ShouldBeFalse();
        (await fixture.FindAsync(other.JobId))!.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task FencedFailureAndSupersede_ShouldRejectStaleClaimTokens()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var failedJob = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "fail");
        var supersededJob = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "supersede");
        await fixture.InsertAsync(failedJob, supersededJob);
        var failedToken = Guid.NewGuid();
        var supersededToken = Guid.NewGuid();

        (await fixture.Repository.TryClaimAsync(
            failedJob.JobId, failedToken, TimeSpan.FromMinutes(5))).ShouldBeTrue();
        (await fixture.Repository.TryClaimAsync(
            supersededJob.JobId, supersededToken, TimeSpan.FromMinutes(5))).ShouldBeTrue();

        (await fixture.Repository.MarkAsFailedAsync(
            failedJob.JobId, Guid.NewGuid(), "STALE", "wrong owner")).ShouldBeFalse();
        (await fixture.Repository.MarkAsSupersededAsync(
            supersededJob.JobId, Guid.NewGuid(), "wrong owner")).ShouldBeFalse();

        (await fixture.FindAsync(failedJob.JobId))!.IsActive.ShouldBeTrue();
        (await fixture.FindAsync(supersededJob.JobId))!.IsActive.ShouldBeTrue();

        (await fixture.Repository.MarkAsFailedAsync(
            failedJob.JobId, failedToken, "EXPECTED", "owned failure")).ShouldBeTrue();
        (await fixture.Repository.MarkAsSupersededAsync(
            supersededJob.JobId, supersededToken, "owned supersede")).ShouldBeTrue();

        var persistedFailed = await fixture.FindAsync(failedJob.JobId);
        persistedFailed!.DispatchStatus.ShouldBe(InstanceJobDispatchStatus.Failed);
        persistedFailed.ProcessingToken.ShouldBeNull();
        var persistedSuperseded = await fixture.FindAsync(supersededJob.JobId);
        persistedSuperseded!.DispatchStatus.ShouldBe(InstanceJobDispatchStatus.Superseded);
        persistedSuperseded.ProcessingToken.ShouldBeNull();
    }

    [Fact]
    public async Task ReleaseClaimAsync_ShouldOnlyReleaseTheCurrentOwnerAndRemainActive()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var job = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "shutdown");
        await fixture.InsertAsync(job);
        var processingToken = Guid.NewGuid();

        (await fixture.Repository.TryClaimAsync(
            job.JobId, processingToken, TimeSpan.FromMinutes(5))).ShouldBeTrue();
        (await fixture.Repository.ReleaseClaimAsync(
            job.JobId, Guid.NewGuid())).ShouldBeFalse();
        (await fixture.Repository.ReleaseClaimAsync(
            job.JobId, processingToken)).ShouldBeTrue();

        var persisted = await fixture.FindAsync(job.JobId);
        persisted!.IsActive.ShouldBeTrue();
        persisted.DispatchStatus.ShouldBe(InstanceJobDispatchStatus.Scheduled);
        persisted.ProcessingAt.ShouldBeNull();
        persisted.ProcessingLeaseUntil.ShouldBeNull();
        persisted.ProcessingToken.ShouldBeNull();
        persisted.Payload.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetInstanceIdsWithActiveJobAsync_ShouldOnlyReturnLiveChainDrivingJobs()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var now = DateTime.UtcNow;
        var cutoff = now.AddMinutes(-10);

        var scheduledRecent = CreateScheduledAsyncJob(Guid.NewGuid(), Guid.NewGuid(), "scheduled-recent");
        var scheduledStale = CreateScheduledAsyncJob(Guid.NewGuid(), Guid.NewGuid(), "scheduled-stale");
        scheduledStale.CreatedAt = now.AddMinutes(-30);
        var pendingRecent = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "pending-recent");
        var pendingStale = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "pending-stale");
        pendingStale.CreatedAt = now.AddMinutes(-30);
        var processingLive = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "processing-live");
        var processingExpired = CreateAdmissionJob(Guid.NewGuid(), Guid.NewGuid(), "processing-expired");
        var unrelatedScheduled = InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForScheduledTransition(Guid.NewGuid(), "waiting", "timer"),
            Guid.NewGuid(),
            "core",
            "test-flow",
            Guid.NewGuid());

        await fixture.InsertAsync(
            scheduledRecent,
            scheduledStale,
            pendingRecent,
            pendingStale,
            processingLive,
            processingExpired,
            unrelatedScheduled);

        (await fixture.Repository.TryClaimAsync(
            processingLive.JobId, Guid.NewGuid(), TimeSpan.FromMinutes(5))).ShouldBeTrue();
        (await fixture.Repository.TryClaimAsync(
            processingExpired.JobId, Guid.NewGuid(), TimeSpan.FromMinutes(5))).ShouldBeTrue();
        await fixture.Context.InstanceJobs
            .Where(item => item.JobId == processingExpired.JobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingLeaseUntil, now.AddMinutes(-1)));

        var allIds = new[]
        {
            scheduledRecent.InstanceId,
            scheduledStale.InstanceId,
            pendingRecent.InstanceId,
            pendingStale.InstanceId,
            processingLive.InstanceId,
            processingExpired.InstanceId,
            unrelatedScheduled.InstanceId
        };
        var live = await fixture.Repository.GetInstanceIdsWithActiveJobAsync(
            allIds, cutoff, now);

        live.ShouldBe(new HashSet<Guid>
        {
            scheduledRecent.InstanceId,
            pendingRecent.InstanceId,
            processingLive.InstanceId
        }, ignoreOrder: true);
    }

    private static InstanceJob CreateAdmissionJob(Guid instanceId, Guid jobId, string transitionKey)
    {
        var admissionToken = Guid.NewGuid();
        return InstanceJob.CreateTransitionAdmission(
            Guid.NewGuid(),
            JobName.ForAsyncTransition(instanceId, "waiting", transitionKey),
            jobId,
            "core",
            "test-flow",
            instanceId,
            "{\"instanceId\":\"test\"}",
            admissionToken,
            admittedRevision: 1);
    }

    private static InstanceJob CreateScheduledAsyncJob(Guid instanceId, Guid jobId, string transitionKey)
        => InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForAsyncTransition(instanceId, "waiting", transitionKey),
            jobId,
            "core",
            "test-flow",
            instanceId);

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        private RepositoryFixture(
            SqliteConnection connection,
            JobRepositoryTestDbContext context,
            ServiceProvider serviceProvider,
            EfCoreInstanceJobRepository repository)
        {
            _connection = connection;
            Context = context;
            _serviceProvider = serviceProvider;
            Repository = repository;
        }

        public JobRepositoryTestDbContext Context { get; }
        public EfCoreInstanceJobRepository Repository { get; }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<WorkflowDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new JobRepositoryTestDbContext(options);
            await context.Database.EnsureCreatedAsync();

            var provider = Substitute.For<IAetherDbContextProvider<WorkflowDbContext>>();
            provider.GetDbContextAsync(Arg.Any<CancellationToken>()).Returns(context);

            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var repository = new EfCoreInstanceJobRepository(provider, serviceProvider);
            return new RepositoryFixture(connection, context, serviceProvider, repository);
        }

        public async Task InsertAsync(params InstanceJob[] jobs)
        {
            await Context.InstanceJobs.AddRangeAsync(jobs);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public Task<InstanceJob?> FindAsync(Guid jobId) =>
            Context.InstanceJobs
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.JobId == jobId);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
            await _serviceProvider.DisposeAsync();
        }
    }

    /// <summary>
    /// The production context contains PostgreSQL-only mappings unrelated to InstanceJob. Keep the
    /// production InstanceJob mapping and remove the rest so this focused relational test can use
    /// SQLite's real ExecuteUpdate implementation without changing production configuration.
    /// </summary>
    private sealed class JobRepositoryTestDbContext(DbContextOptions<WorkflowDbContext> options)
        : WorkflowDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            foreach (var entityType in builder.Model.GetEntityTypes().ToList())
            {
                if (entityType.ClrType != typeof(InstanceJob))
                    builder.Ignore(entityType.ClrType);
            }
        }
    }
}
