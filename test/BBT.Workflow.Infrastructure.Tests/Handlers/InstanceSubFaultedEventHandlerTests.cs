using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Tracing;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Workers.Inbox.Forwarding;
using BBT.Workflow.Workers.Inbox.Handlers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Handlers;

/// <summary>
/// Pins the domain guard of the Inbox sub-fault backup (vnext#1256). The worker forwards only to its
/// own Orchestration, so a fault whose parent lives in another domain has no backup here: the
/// child's post-commit relay is its only delivery, and the drop is the one trace left when a parent
/// never heard of the fault. It was logged at Debug.
/// </summary>
public sealed class InstanceSubFaultedEventHandlerTests
{
    private const int DroppedEventId = 40111;

    private readonly IRuntimeInfoProvider _runtime = Substitute.For<IRuntimeInfoProvider>();
    private readonly IOrchestrationForwarder _forwarder = Substitute.For<IOrchestrationForwarder>();
    private readonly RecordingLogger _logger = new();

    [Fact]
    public async Task HandleAsync_WhenTheParentDomainIsNotHostedHere_WarnsAndForwardsNothing()
    {
        _runtime.IsDomainMatch("parent-domain").Returns(false);
        _runtime.Domain.Returns("child-domain");

        await CreateHandler().HandleAsync(Envelope(FaultedEvent()), CancellationToken.None);

        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.EventId.ShouldBe(DroppedEventId);
        entry.Message.ShouldContain("parent-domain");
        entry.Message.ShouldContain("child-domain");
        await _forwarder.DidNotReceiveWithAnyArgs().ForwardAsync<SubFlowFaultedInput>(
            default!, default!, default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_WhenTheParentDomainIsHostedHere_ForwardsTheBackupToSubFault()
    {
        _runtime.IsDomainMatch("parent-domain").Returns(true);
        var evt = FaultedEvent();

        await CreateHandler().HandleAsync(Envelope(evt), CancellationToken.None);

        // Sync is forced off: the backup must never block the worker on an inline parent chain.
        await _forwarder.Received(1).ForwardAsync(
            HttpMethod.Post,
            $"api/v1/parent-domain/workflows/parent-flow/instances/{evt.InstanceId}/sub/fault",
            Arg.Is<SubFlowFaultedInput>(body =>
                body.InstanceId == evt.InstanceId && body.SubInstanceId == evt.SubInstanceId && !body.Sync),
            "parent-domain",
            "parent-flow",
            "1.0.0",
            evt.InstanceId,
            Arg.Any<CancellationToken>(),
            Arg.Any<RuntimeCallTarget?>());
        _logger.Entries.ShouldNotContain(entry => entry.EventId == DroppedEventId);
    }

    private InstanceSubFaultedEventHandler CreateHandler() =>
        new(_runtime, _forwarder, Substitute.For<ICorrelationIdProvider>(), _logger);

    private static InstanceSubFaultedEvent FaultedEvent() => new()
    {
        InstanceId = Guid.NewGuid(),
        SubInstanceId = Guid.NewGuid(),
        Domain = "parent-domain",
        Flow = "parent-flow",
        Version = "1.0.0",
        FaultedState = "failed",
        FaultedAt = DateTime.UtcNow,
        SubFlowName = "child-flow",
        Sync = true
    };

    private static CloudEventEnvelope<InstanceSubFaultedEvent> Envelope(InstanceSubFaultedEvent evt) =>
        new() { Id = Guid.NewGuid().ToString(), Data = evt };

    private sealed class RecordingLogger : ILogger<InstanceSubFaultedEventHandler>
    {
        public List<(LogLevel Level, int EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
    }
}
