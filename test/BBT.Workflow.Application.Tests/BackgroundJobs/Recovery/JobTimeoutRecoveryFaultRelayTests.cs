using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.BackgroundJobs.Recovery;

/// <summary>
/// Pins the relay of a job-timeout fault (vnext#1256). The recovery faults the instance in a unit of
/// work no runner relays from, so a sub item's upward <see cref="InstanceSubFaultedEvent"/> travelled
/// the outbox alone, and the child domain's Inbox drops it when the parent lives in another domain.
/// </summary>
public sealed class JobTimeoutRecoveryFaultRelayTests
{
    private readonly List<string> _order = [];
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceTransitionRepository _transitionRepository = Substitute.For<IInstanceTransitionRepository>();
    private readonly IPostCommitRelayDispatcher _dispatcher = Substitute.For<IPostCommitRelayDispatcher>();
    private IReadOnlyList<DomainEventEnvelope> _relayed = [];
    private CancellationToken _relayToken;

    public JobTimeoutRecoveryFaultRelayTests()
    {
        _uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _order.Add("commit");
            return Task.CompletedTask;
        });
        _uow.DisposeAsync().Returns(_ =>
        {
            _order.Add("uow-disposed");
            return ValueTask.CompletedTask;
        });
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_uow);
        // The real save drains the aggregate's events into the outbox (SaveChanges sink), so the
        // relay only sees them if the service snapshots BEFORE saving.
        _instanceRepository
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var saved = call.ArgAt<Instance>(0);
                saved.ClearDomainEvents();
                return Task.FromResult(saved);
            });
        _dispatcher.RelayAsync(Arg.Any<IReadOnlyList<DomainEventEnvelope>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _order.Add("relay");
                _relayed = call.ArgAt<IReadOnlyList<DomainEventEnvelope>>(0);
                _relayToken = call.ArgAt<CancellationToken>(1);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task FaultInstanceAsync_WhenTheInstanceIsASubItem_RelaysItsSubFaultedEvent_AfterTheCommit()
    {
        var parentId = Guid.NewGuid();
        var instance = BusySubFlowInstance(parentId);
        _instanceRepository.FindAsync(instance.Id, true, Arg.Any<CancellationToken>()).Returns(instance);

        await CreateService().FaultInstanceAsync(Payload(instance.Id), CancellationToken.None);

        _order.ShouldBe(new[] { "commit", "uow-disposed", "relay" });
        var evt = _relayed.Select(e => e.Event).OfType<InstanceSubFaultedEvent>().Single();
        evt.InstanceId.ShouldBe(parentId);
        evt.SubInstanceId.ShouldBe(instance.Id);
        evt.Domain.ShouldBe("parent-domain");
        // The recovery's own token is None on purpose; the relay is bounded instead.
        _relayToken.CanBeCanceled.ShouldBeTrue();
    }

    [Fact]
    public async Task FaultInstanceAsync_WhenTheCommitFails_RelaysNothing()
    {
        var instance = BusySubFlowInstance(Guid.NewGuid());
        _instanceRepository.FindAsync(instance.Id, true, Arg.Any<CancellationToken>()).Returns(instance);
        _uow.CommitAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("commit failed"));

        await CreateService().FaultInstanceAsync(Payload(instance.Id), CancellationToken.None);

        await _dispatcher.DidNotReceiveWithAnyArgs().RelayAsync(default!, default);
    }

    [Fact]
    public async Task FaultInstanceAsync_WhenTheInstanceIsNotBusy_RelaysNothing()
    {
        var instance = Instance.Create(Guid.NewGuid(), "child-flow", "1.0.0", "key");
        _instanceRepository.FindAsync(instance.Id, true, Arg.Any<CancellationToken>()).Returns(instance);

        await CreateService().FaultInstanceAsync(Payload(instance.Id), CancellationToken.None);

        await _dispatcher.DidNotReceiveWithAnyArgs().RelayAsync(default!, default);
    }

    private JobTimeoutRecoveryService CreateService() => new(
        _uowManager,
        _instanceRepository,
        _transitionRepository,
        Options.Create(new WorkflowExecutionOptions { TransitionJobTimeoutSeconds = 300 }),
        NullLogger<JobTimeoutRecoveryService>.Instance,
        relayDispatcher: _dispatcher);

    private static TransitionJobPayload Payload(Guid instanceId) => new()
    {
        JobName = "trans-abc-go",
        InstanceId = instanceId,
        TransitionKey = "go",
        Domain = "child-domain",
        Workflow = "child-flow",
        Version = "1.0.0"
    };

    private static Instance BusySubFlowInstance(Guid parentId)
    {
        var instance = Instance.Create(Guid.NewGuid(), "child-flow", "1.0.0", "key");
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubFlow.Code;
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Id] = parentId.ToString();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Domain] = "parent-domain";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Flow] = "parent-flow";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Version] = "1.0.0";
        instance.Busy();
        return instance;
    }
}
