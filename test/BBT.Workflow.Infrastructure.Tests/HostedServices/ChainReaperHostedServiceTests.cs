using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Infrastructure.Execution.Locks;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.HostedServices;

public sealed class ChainReaperHostedServiceTests
{
    private const int LeaderLeaseSeconds = 47;

    [Fact]
    public async Task Leader_cycle_skips_the_sweep_when_postgres_leadership_is_unavailable()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var lockService = Substitute.For<IPostgreSqlDistributedLockService>();
        using var cancellation = new CancellationTokenSource();
        lockService.TryAcquireLockAsync("chain-reaper-leader", LeaderLeaseSeconds, cancellation.Token)
            .Returns(Task.FromResult<IDistributedLockHandle?>(null));
        var service = CreateService(scopeFactory, lockService);

        await RunLeaderCycleAsync(service, cancellation.Token);

        await lockService.Received(1).TryAcquireLockAsync(
            "chain-reaper-leader", LeaderLeaseSeconds, cancellation.Token);
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task Leader_cycle_runs_discovery_and_disposes_postgres_leadership_when_acquired()
    {
        using var cancellation = new CancellationTokenSource();
        var lockHandle = Substitute.For<IDistributedLockHandle>();
        lockHandle.DisposeAsync().Returns(ValueTask.CompletedTask);
        var lockService = Substitute.For<IPostgreSqlDistributedLockService>();
        lockService.TryAcquireLockAsync("chain-reaper-leader", LeaderLeaseSeconds, cancellation.Token)
            .Returns(Task.FromResult<IDistributedLockHandle?>(lockHandle));

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.DisposeAsync().Returns(ValueTask.CompletedTask);
        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        unitOfWorkManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(unitOfWork);
        var instanceRepository = Substitute.For<IInstanceRepository>();
        instanceRepository.GetActiveFlowKeysAsync(cancellation.Token)
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));

        var services = new ServiceCollection();
        services.AddSingleton(unitOfWorkManager);
        services.AddSingleton(instanceRepository);
        using var provider = services.BuildServiceProvider();
        var service = CreateService(provider.GetRequiredService<IServiceScopeFactory>(), lockService);

        await RunLeaderCycleAsync(service, cancellation.Token);

        await lockService.Received(1).TryAcquireLockAsync(
            "chain-reaper-leader", LeaderLeaseSeconds, cancellation.Token);
        await instanceRepository.Received(1).GetActiveFlowKeysAsync(cancellation.Token);
        await lockHandle.Received(1).DisposeAsync();
    }

    private static ChainReaperHostedService CreateService(
        IServiceScopeFactory scopeFactory,
        IPostgreSqlDistributedLockService lockService) =>
        new(
            scopeFactory,
            lockService,
            Options.Create(new WorkflowExecutionOptions
            {
                ChainReaperLeaderLeaseSeconds = LeaderLeaseSeconds
            }),
            Substitute.For<ILogger<ChainReaperHostedService>>());

    private static async Task RunLeaderCycleAsync(
        ChainReaperHostedService service,
        CancellationToken cancellationToken)
    {
        var method = typeof(ChainReaperHostedService).GetMethod(
            "RunLeaderSweepAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();

        var task = method!.Invoke(service, [cancellationToken]);
        (task is Task).ShouldBeTrue();
        await (Task)task!;
    }
}
