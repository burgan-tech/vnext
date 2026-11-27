using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using BBT.Workflow.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// vnext#1252: <see cref="InstanceBusyManager.TryUnfaultIntoBusyAsync"/>, the reserved cancel's takeover of a Faulted
/// instance. Faulted → Busy, incidents resolved, open records closed — committed together, or nothing at all.
/// </summary>
public sealed class InstanceBusyManagerUnfaultForCancelTests
{
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceIncidentRepository _incidentRepository = Substitute.For<IInstanceIncidentRepository>();
    private readonly IInstanceTransitionRepository _transitionRepository = Substitute.For<IInstanceTransitionRepository>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly Instance _instance = Instance.Create(Guid.NewGuid(), "order-flow", "1.0.0");

    public InstanceBusyManagerUnfaultForCancelTests()
    {
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_uow);
        _instanceRepository.TryUnfaultAsync(_instance, Arg.Any<CancellationToken>()).Returns(true);
        _instanceRepository.TryMarkBusyAsync(_instance, Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    public async Task TakesTheInstanceOver_InOneTransaction()
    {
        var taken = await Sut().TryUnfaultIntoBusyAsync(_instance, CancellationToken.None);

        taken.ShouldBeTrue();
        _uowManager.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(o =>
            o.IsTransactional && o.Scope == UnitOfWorkScopeOption.RequiresNew));
        Received.InOrder(() =>
        {
            _instanceRepository.LoadActiveIncidentsAsync(_instance, Arg.Any<CancellationToken>());
            _instanceRepository.TryUnfaultAsync(_instance, Arg.Any<CancellationToken>());
            _instanceRepository.TryMarkBusyAsync(_instance, Arg.Any<CancellationToken>());
            _incidentRepository.ResolveAllAsync(_instance.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
            _transitionRepository.FailOpenAsync(_instance.Id, Arg.Any<CancellationToken>());
            _uow.CommitAsync(Arg.Any<CancellationToken>());
            _uow.DisposeAsync();
            // The caller's copy is aligned only after the takeover's own unit of work is closed: before that the
            // repository would resolve the takeover's change tracker, not the caller's.
            _instanceRepository.AcceptPersistedStatusAsync(_instance, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task NoLongerFaulted_CommitsNothing()
    {
        // A retry or another cancel took the instance first.
        _instanceRepository.TryUnfaultAsync(_instance, Arg.Any<CancellationToken>()).Returns(false);

        var taken = await Sut().TryUnfaultIntoBusyAsync(_instance, CancellationToken.None);

        taken.ShouldBeFalse();
        await _instanceRepository.DidNotReceive().TryMarkBusyAsync(_instance, Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive().AcceptPersistedStatusAsync(_instance, Arg.Any<CancellationToken>());
        await _incidentRepository.DidNotReceiveWithAnyArgs().ResolveAllAsync(default, default, default);
        await _transitionRepository.DidNotReceiveWithAnyArgs().FailOpenAsync(default, default);
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BusyFlipLost_CommitsNothing_SoTheUnfaultRollsBack()
    {
        _instanceRepository.TryMarkBusyAsync(_instance, Arg.Any<CancellationToken>()).Returns(false);

        var taken = await Sut().TryUnfaultIntoBusyAsync(_instance, CancellationToken.None);

        taken.ShouldBeFalse();
        await _incidentRepository.DidNotReceiveWithAnyArgs().ResolveAllAsync(default, default, default);
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    private InstanceBusyManager Sut() => new(
        _instanceRepository,
        _incidentRepository,
        _transitionRepository,
        _uowManager,
        Substitute.For<IInstanceCommandGateway>(),
        NullLogger<InstanceBusyManager>.Instance);
}
