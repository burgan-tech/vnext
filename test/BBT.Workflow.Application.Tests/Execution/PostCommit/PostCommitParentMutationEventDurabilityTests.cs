using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Events;
using BBT.Aether.Domain.Services;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;
using DurableOutboxMessage = BBT.Aether.Domain.Events.OutboxMessage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.PostCommit;

public sealed class PostCommitParentMutationEventDurabilityTests
{
    [Fact]
    public async Task FaultAsync_StagesGeneratedCleanupEventThroughTheOwningUowOutbox()
    {
        await using var harness = await FaultEventHarness.CreateAsync();

        var result = await harness.Service.FaultAsync(
            harness.Snapshot,
            new PostCommitFaultRequest("PostCommit:Failure", "child invocation failed"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(InstanceStatus.Faulted);
        harness.DispatchedEvents.ShouldHaveSingleItem()
            .Event.ShouldBeOfType<InstanceFaultedCleanupEvent>();
        (await harness.CountOutboxRowsAsync()).ShouldBe(1);
        await harness.Dispatcher.DidNotReceive().PublishDirectlyAsync(
            Arg.Any<IEnumerable<DomainEventEnvelope>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FaultAsync_WhenOutboxSaveFails_RollsBackTheGeneratedEventWithoutExternalPublication()
    {
        await using var harness = await FaultEventHarness.CreateAsync(failOnSaveAttempt: 2);

        await Should.ThrowAsync<InvalidOperationException>(() => harness.Service.FaultAsync(
            harness.Snapshot,
            new PostCommitFaultRequest("PostCommit:Failure", "child invocation failed"),
            CancellationToken.None));

        harness.DispatchedEvents.ShouldHaveSingleItem()
            .Event.ShouldBeOfType<InstanceFaultedCleanupEvent>();
        (await harness.CountOutboxRowsAsync()).ShouldBe(0);
        await harness.Dispatcher.DidNotReceive().PublishDirectlyAsync(
            Arg.Any<IEnumerable<DomainEventEnvelope>>(),
            Arg.Any<CancellationToken>());
    }

    private sealed class FaultEventHarness : IAsyncDisposable
    {
        private const string Domain = "fault-events";
        private const string WorkflowKey = "test-workflow";
        private const string WorkflowVersion = "1.0.0";
        private readonly SqliteConnection _anchor;
        private readonly CompositeUnitOfWork _uow;
        private readonly ServiceProvider _provider;

        private FaultEventHarness(
            ServiceProvider provider,
            SqliteConnection anchor,
            CompositeUnitOfWork uow,
            PostCommitParentMutationService service,
            IDomainEventDispatcher dispatcher,
            List<DomainEventEnvelope> dispatchedEvents,
            PostCommitParentSnapshot snapshot)
        {
            _provider = provider;
            _anchor = anchor;
            _uow = uow;
            Service = service;
            Dispatcher = dispatcher;
            DispatchedEvents = dispatchedEvents;
            Snapshot = snapshot;
        }

        public PostCommitParentMutationService Service { get; }
        public IDomainEventDispatcher Dispatcher { get; }
        public List<DomainEventEnvelope> DispatchedEvents { get; }
        public PostCommitParentSnapshot Snapshot { get; }

        public static async Task<FaultEventHarness> CreateAsync(int failOnSaveAttempt = 0)
        {
            var schema = new StaticCurrentSchema(Domain);
            var store = new FaultEventStore(failOnSaveAttempt);
            var services = new ServiceCollection();
            services.AddSingleton<ICurrentSchema>(schema);
            services.AddSingleton(store);
            services.AddSingleton<IAetherDbContextConfigurator<FaultEventDbContext>, SqliteConfigurator>();
            var provider = services.BuildServiceProvider();
            var configurator = provider.GetRequiredService<IAetherDbContextConfigurator<FaultEventDbContext>>();
            var anchor = ((SqliteConfigurator)configurator).Anchor;
            await using (var database = new FaultEventDbContext(
                             new DbContextOptionsBuilder<FaultEventDbContext>().UseSqlite(anchor).Options,
                             store))
            {
                await database.Database.EnsureCreatedAsync();
            }

            var authoritative = Instance.Create(Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
            await using (var seed = new FaultEventDbContext(
                             new DbContextOptionsBuilder<FaultEventDbContext>().UseSqlite(anchor).Options,
                             new FaultEventStore()))
            {
                seed.Instances.Add(authoritative);
                await seed.SaveChangesAsync();
            }
            authoritative.BeginChain(Guid.NewGuid());

            var dispatcher = Substitute.For<IDomainEventDispatcher>();
            var dispatchedEvents = new List<DomainEventEnvelope>();
            CompositeUnitOfWork? uow = null;
            dispatcher.DispatchEventsAsync(
                    Arg.Any<IEnumerable<DomainEventEnvelope>>(),
                    Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var events = call.ArgAt<IEnumerable<DomainEventEnvelope>>(0).ToList();
                    dispatchedEvents.AddRange(events);
                    var context = await uow!.GetDbContextAsync<FaultEventDbContext>(Domain);
                    foreach (var envelope in events)
                    {
                        context.OutboxMessages.Add(new DurableOutboxMessage(
                            Guid.NewGuid(),
                            envelope.Metadata.EventName,
                            []));
                    }
                });

            uow = new CompositeUnitOfWork(
                provider,
                dispatcher,
                new AetherDomainEventOptions { DispatchStrategy = DomainEventDispatchStrategy.AlwaysUseOutbox });
            await uow.InitializeAsync(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = true
            });

            var repository = Substitute.For<IInstanceRepository>();
            repository.FindWithAllCorrelationsAndDataAsync(authoritative.Id, Arg.Any<CancellationToken>())
                .Returns(authoritative);
            repository.UpdateAsync(authoritative, true, Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    var context = await uow.GetDbContextAsync<FaultEventDbContext>(Domain);
                    context.Update(authoritative);
                    return authoritative;
                });

            var lockScope = Substitute.For<ITransitionLockScope>();
            lockScope.IsAcquired.Returns(true);
            var lockFactory = Substitute.For<ITransitionLockScopeFactory>();
            lockFactory.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(lockScope);

            var uowManager = Substitute.For<IUnitOfWorkManager>();
            uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(uow);
            var workflowContext = Substitute.For<IWorkflowContext>();
            workflowContext.Workflow.Returns(CreateWorkflow());
            var service = new PostCommitParentMutationService(
                uowManager,
                repository,
                lockFactory,
                workflowContext,
                Substitute.For<IStateNotificationScheduler>(),
                NullLogger<PostCommitParentMutationService>.Instance);
            var snapshot = new PostCommitParentSnapshot(
                Domain,
                WorkflowKey,
                WorkflowVersion,
                authoritative.Id,
                "source-transition",
                ExecMode.Sync,
                "trace-id",
                new Dictionary<string, string?>(),
                new Dictionary<string, string?>(),
                null);

            return new FaultEventHarness(
                provider,
                anchor,
                uow,
                service,
                dispatcher,
                dispatchedEvents,
                snapshot);
        }

        public async Task<int> CountOutboxRowsAsync()
        {
            await using var context = new FaultEventDbContext(
                new DbContextOptionsBuilder<FaultEventDbContext>().UseSqlite(_anchor).Options,
                new FaultEventStore());
            return await context.OutboxMessages.CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _uow.DisposeAsync();
            await _anchor.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private sealed class FaultEventStore(int failOnSaveAttempt = 0)
    {
        public int FailOnSaveAttempt { get; } = failOnSaveAttempt;
        public int SaveAttempts { get; set; }
    }

    private sealed class FaultEventDbContext(
        DbContextOptions<FaultEventDbContext> options,
        FaultEventStore store)
        : AetherDbContext<FaultEventDbContext>(options)
    {
        public DbSet<Instance> Instances => Set<Instance>();
        public DbSet<DurableOutboxMessage> OutboxMessages => Set<DurableOutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<InstanceCorrelation>();
            modelBuilder.Ignore<InstanceData>();
            modelBuilder.Ignore<SubFlowType>();
            modelBuilder.Entity<Instance>(entity =>
            {
                entity.ToTable("Instances");
                entity.HasKey(instance => instance.Id);
                entity.Ignore(instance => instance.HasKey);
                entity.Ignore(instance => instance.GetCurrentState);
                entity.Ignore(instance => instance.GetEffectiveState);
                entity.Ignore(instance => instance.IsCompleted);
                entity.Ignore(instance => instance.IsBusy);
                entity.Ignore(instance => instance.IsActive);
                entity.Ignore(instance => instance.IsSubFlow);
                entity.Ignore(instance => instance.IsSubItem);
                entity.Ignore(instance => instance.HasActiveSubFlow);
                entity.Ignore(instance => instance.IsDataPartiallyLoaded);
                entity.Ignore(instance => instance.IsAwaitingLongPollAck);
                entity.Ignore(instance => instance.ExtraProperties);
                entity.Ignore(instance => instance.Tags);
                entity.Ignore(instance => instance.Data);
                entity.Ignore(instance => instance.LatestData);
                entity.Ignore(instance => instance.DataList);
                entity.Ignore(instance => instance.ChildCorrelations);
                entity.Ignore(instance => instance.ActiveCorrelations);
                entity.Ignore(instance => instance.Subflow);
                entity.Ignore(instance => instance.Status);
            });

            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureOutbox();
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            store.SaveAttempts++;
            if (store.FailOnSaveAttempt == store.SaveAttempts)
                return Task.FromException<int>(new InvalidOperationException("outbox save failed"));

            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    private sealed class SqliteConfigurator : IAetherDbContextConfigurator<FaultEventDbContext>
    {
        private readonly string _connectionString =
            $"Data Source=fault-events-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        public SqliteConfigurator()
        {
            Anchor = new SqliteConnection(_connectionString);
            Anchor.Open();
        }

        public SqliteConnection Anchor { get; }

        public DbConnection CreateConnection() => new SqliteConnection(_connectionString);

        public DbContextOptions<FaultEventDbContext> BuildOptions(
            DbConnection sharedConnection,
            string schema,
            SchemaScopeState state) => new DbContextOptionsBuilder<FaultEventDbContext>()
            .UseSqlite((SqliteConnection)sharedConnection)
            .Options;

        public DbContextOptions<FaultEventDbContext> BuildOwnedOptions(string schema) =>
            new DbContextOptionsBuilder<FaultEventDbContext>()
                .UseSqlite(_connectionString)
                .Options;
    }

    private static WorkflowDefinition CreateWorkflow()
    {
        var workflow = WorkflowDefinition.Create();
        workflow.SetReference(new Reference("test-workflow", "fault-events", "sys-flows", "1.0.0"));
        return workflow;
    }
}
