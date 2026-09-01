using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Workflow.Discovery;
using BBT.Workflow.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HostedServices.Discovery;

/// <summary>
/// Pins the once-per-rollout registration guard on <see cref="DomainDiscoveryInitializationHostedService"/>.
/// Registration stays (every service must register itself). But 20 identical replicas registering
/// the same thing on every rollout is pointless, so a non-blocking, non-retrying distributed lock
/// picks the single replica that performs it.
/// <para>
/// Exercised via the internal <see cref="DomainDiscoveryInitializationHostedService.RunAsync"/> seam
/// rather than <c>BackgroundService.StartAsync</c>: <c>StartAsync</c> invokes <c>ExecuteAsync</c> in a
/// way that is not guaranteed to run synchronously on the calling thread even when every awaited call
/// completes immediately (confirmed empirically — a direct <c>await StartAsync(...)</c> raced ahead
/// of the method body actually running), which made assertions against it nondeterministic. Calling
/// <c>RunAsync</c> directly is a plain async method call with normal, deterministic semantics.
/// </para>
/// <para>
/// The five behaviors below are the whole contract:
/// <list type="number">
/// <item>lock acquired → registration runs;</item>
/// <item>lock NOT acquired → registration is skipped, no exception (a replica that lost the race
/// must start normally, not abort);</item>
/// <item>lock acquired and registration throws → the lock is released (so another replica can try
/// immediately) and the exception still propagates (this pod aborts startup and is restarted);</item>
/// <item>lock acquired and registration succeeds → the lock is <b>NOT</b> released — neither via
/// <c>ReleaseAsync</c> nor via <c>DisposeAsync</c> (the regression a well-meaning "tidy this into
/// <c>await using</c>" refactor would introduce). This is the counter-intuitive, load-bearing case:
/// the lease is left to expire so the next replica in the same rollout (which starts
/// seconds-to-minutes later, not concurrently) does not see a free lock and re-register;</item>
/// <item>service discovery disabled → the lock is never attempted and registration is never
/// called, so a disabled pod cannot strand the lease for a later enabled one.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DomainDiscoveryInitializationHostedServiceTests
{
    private static readonly DomainRegistrationIdentity Identity =
        new("lending", "https://lending.internal.test", "https://lending.internal.test/health", Enabled: true);

    private static readonly DomainRegistrationIdentity DisabledIdentity =
        new("lending", "https://lending.internal.test", "https://lending.internal.test/health", Enabled: false);

    private static (
        DomainDiscoveryInitializationHostedService Sut,
        IDomainRegistrationService Registration,
        IDistributedLockService LockService) CreateSut(
        IDistributedLockHandle? acquiredHandle,
        DomainRegistrationIdentity? identity = null)
    {
        var registrationService = Substitute.For<IDomainRegistrationService>();
        registrationService.GetRegistrationIdentity().Returns(identity ?? Identity);
        registrationService.RegisterDomainAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var lockService = Substitute.For<IDistributedLockService>();
        lockService.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(acquiredHandle));

        var services = new ServiceCollection();
        services.AddSingleton(registrationService);
        services.AddSingleton(lockService);
        var provider = services.BuildServiceProvider();

        var sut = new DomainDiscoveryInitializationHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DomainDiscoveryInitializationHostedService>.Instance);

        return (sut, registrationService, lockService);
    }

    [Fact]
    public async Task RunAsync_WhenLockAcquired_RunsRegistration()
    {
        var handle = Substitute.For<IDistributedLockHandle>();
        var (sut, registration, lockService) = CreateSut(handle);

        await sut.RunAsync(CancellationToken.None);

        await registration.Received(1).RegisterDomainAsync(Arg.Any<CancellationToken>());
        await lockService.Received(1).TryAcquireLockAsync(
            Arg.Is<string>(k => k.StartsWith($"discovery:register:{Identity.DomainName}:", StringComparison.Ordinal)),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenLockNotAcquired_SkipsRegistration_AndCompletesWithoutThrowing()
    {
        var (sut, registration, _) = CreateSut(acquiredHandle: null);

        // Must not throw: a replica that lost the race starts normally, it does not abort startup.
        await sut.RunAsync(CancellationToken.None);

        await registration.DidNotReceive().RegisterDomainAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenLockAcquiredAndRegistrationThrows_ReleasesLock_AndRethrows()
    {
        var handle = Substitute.For<IDistributedLockHandle>();
        var (sut, registration, _) = CreateSut(handle);

        var failure = new InvalidOperationException("registry unreachable");
        registration.RegisterDomainAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(failure));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => sut.RunAsync(CancellationToken.None));
        thrown.ShouldBeSameAs(failure);

        await handle.Received(1).ReleaseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenLockAcquiredAndRegistrationSucceeds_DoesNotReleaseLock()
    {
        var handle = Substitute.For<IDistributedLockHandle>();
        var (sut, _, _) = CreateSut(handle);

        await sut.RunAsync(CancellationToken.None);

        // The heart of the design: the lease is left to expire on success. Releasing here would
        // let the next replica in the same rollout (seconds-to-minutes later) re-acquire and
        // re-register, defeating the once-per-rollout guard entirely. Both release paths are
        // pinned: ReleaseAsync directly, and DisposeAsync — the method a "tidy this into `await
        // using var handle = ...`" refactor would call instead, which would silently reintroduce
        // the same regression without this assertion catching it.
        await handle.DidNotReceive().ReleaseAsync(Arg.Any<CancellationToken>());
        await handle.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task RunAsync_WhenDiscoveryDisabled_SkipsLockAndRegistration()
    {
        var (sut, registration, lockService) = CreateSut(acquiredHandle: null, identity: DisabledIdentity);

        // Must not throw either: a pod with discovery disabled starts normally.
        await sut.RunAsync(CancellationToken.None);

        // No lock attempt at all — not just a skipped registration call. Taking the lock here
        // would let a disabled pod strand the once-per-rollout lease: during an
        // Enabled:false->true config rollout the base URL is unchanged, so the lock key is
        // identical, and a still-disabled pod winning the lease would make the newly-enabled pod
        // skip with nothing left to re-register until the next deploy.
        await lockService.DidNotReceive().TryAcquireLockAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await registration.DidNotReceive().RegisterDomainAsync(Arg.Any<CancellationToken>());
    }
}
