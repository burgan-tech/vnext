using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Infrastructure.EventBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.EventBus;

/// <summary>
/// Unit tests for the additive-hook behavior of <see cref="HookedDistributedEventBus"/>.
/// Legacy: a fully successful hook marks the event handled and the inner bus (outbox) is skipped.
/// Additive: the hook runs AND the event is still published to the inner bus, restoring the
/// documented dual-processing pattern (hook + distributed handler both run).
/// </summary>
public sealed class HookedDistributedEventBusAdditiveTests
{
    /// <summary>An event type that opts into hooks via <see cref="EventHookAttribute"/>.</summary>
    [EventHook]
    private sealed class HookedTestEvent;

    /// <summary>Records whether the inner bus was asked to publish.</summary>
    private sealed class RecordingInnerBus : IDistributedEventBus
    {
        public int GenericPublishCount { get; private set; }

        public Task PublishAsync<TEvent>(TEvent payload, string? subject = null,
            CancellationToken cancellationToken = default) where TEvent : class
        {
            GenericPublishCount++;
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(TEvent payload, string? subject, bool useOutbox,
            CancellationToken cancellationToken = default) where TEvent : class
        {
            GenericPublishCount++;
            return Task.CompletedTask;
        }

        public Task PublishAsync(IDistributedEvent @event, EventMetadata metadata, string? subject = null,
            bool useOutbox = true, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishEnvelopeAsync(byte[] serializedEnvelope, string topicName, string pubSubName,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>A hook invoker that always succeeds for <see cref="HookedTestEvent"/>.</summary>
    private sealed class AlwaysSucceedInvoker : IEventHookInvoker
    {
        public Type EventType => typeof(HookedTestEvent);
        public string HookName => nameof(AlwaysSucceedInvoker);

        public Task<EventHookResult> InvokeAsync(object eventData, EventHookContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EventHookResult.Ok());
    }

    private static (HookedDistributedEventBus bus, RecordingInnerBus inner) CreateBus(bool additive)
    {
        var inner = new RecordingInnerBus();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IEventHookInvoker, AlwaysSucceedInvoker>()
            .BuildServiceProvider();

        var bus = new HookedDistributedEventBus(
            inner,
            serviceProvider,
            NullLogger<HookedDistributedEventBus>.Instance,
            () => additive);

        return (bus, inner);
    }

    [Fact]
    public async Task Legacy_SuccessfulHook_ShouldShortCircuit_AndNotPublishToInner()
    {
        var (bus, inner) = CreateBus(additive: false);

        await bus.PublishAsync(new HookedTestEvent(), subject: null, useOutbox: true, CancellationToken.None);

        inner.GenericPublishCount.ShouldBe(0);
    }

    [Fact]
    public async Task Additive_SuccessfulHook_ShouldStillPublishToInner()
    {
        var (bus, inner) = CreateBus(additive: true);

        await bus.PublishAsync(new HookedTestEvent(), subject: null, useOutbox: true, CancellationToken.None);

        // Hook ran (local side-effect) AND the event was published to the inner bus (outbox)
        // so the distributed handler runs too — the dual-processing pattern.
        inner.GenericPublishCount.ShouldBe(1);
    }
}
