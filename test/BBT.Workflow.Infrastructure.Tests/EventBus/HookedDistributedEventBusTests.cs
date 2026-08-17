using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Tracing;
using BBT.Aether.Uow;
using BBT.Workflow.Events;
using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Infrastructure.EventBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.EventBus;

public sealed class HookedDistributedEventBusTests
{
    [Fact]
    public async Task Publish_TraceableEventWithEmptyFields_StampsAmbientTraceAndCorrelation()
    {
        var calls = new List<string>();
        var correlationProvider = Substitute.For<ICorrelationIdProvider>();
        correlationProvider.Get().Returns("req-123");
        var (sut, _, _, _) = CreateSut(calls, EventHookResult.Ok(), hasAmbientUow: false, correlationProvider);

        var evt = new TraceableEvent();
        var activity = new Activity("publisher");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.TraceStateString = "vendor=state";
        activity.Start();
        try
        {
            await sut.PublishAsync(evt, useOutbox: true);
        }
        finally
        {
            activity.Stop();
            Activity.Current = null;
        }

        evt.TraceParent.ShouldBe(activity.Id);
        evt.TraceState.ShouldBe("vendor=state");
        evt.RequestId.ShouldBe("req-123");
    }

    [Fact]
    public async Task Publish_TraceableEventWithPresetFields_DoesNotOverwrite()
    {
        var calls = new List<string>();
        var correlationProvider = Substitute.For<ICorrelationIdProvider>();
        correlationProvider.Get().Returns("req-123");
        var (sut, _, _, _) = CreateSut(calls, EventHookResult.Ok(), hasAmbientUow: false, correlationProvider);

        var evt = new TraceableEvent
        {
            TraceParent = "00-11111111111111111111111111111111-2222222222222222-01",
            TraceState = "preset=1",
            RequestId = "preset-req"
        };

        var activity = new Activity("publisher");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        try
        {
            await sut.PublishAsync(evt, useOutbox: true);
        }
        finally
        {
            activity.Stop();
            Activity.Current = null;
        }

        evt.TraceParent.ShouldBe("00-11111111111111111111111111111111-2222222222222222-01");
        evt.TraceState.ShouldBe("preset=1");
        evt.RequestId.ShouldBe("preset-req");
    }

    [Fact]
    public async Task DurablePostCommit_WithAmbientUow_PublishesBeforeCommitAndHooksAfterCommit()
    {
        var calls = new List<string>();
        var (sut, inner, invoker, uow) = CreateSut(
            calls,
            EventHookResult.Ok(),
            hasAmbientUow: true);

        await sut.PublishAsync(new DurableEvent(), useOutbox: true);

        calls.ShouldBe(["inner"]);

        await uow.CommitAsync();

        calls.ShouldBe(["inner", "commit", "hook"]);
        await inner.Received(1).PublishAsync(
            Arg.Any<DurableEvent>(),
            Arg.Any<string?>(),
            true,
            Arg.Any<CancellationToken>());
        await invoker.Received(1).InvokeAsync(
            Arg.Any<object>(),
            Arg.Any<EventHookContext>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task DurablePostCommit_WhenHookSucceeds_StillPublishesInnerOnce()
    {
        var calls = new List<string>();
        var (sut, inner, _, uow) = CreateSut(calls, EventHookResult.Ok(), hasAmbientUow: true);

        await sut.PublishAsync(new DurableEvent(), useOutbox: true);
        await uow.CommitAsync();

        await inner.Received(1).PublishAsync(
            Arg.Any<DurableEvent>(),
            Arg.Any<string?>(),
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DurablePostCommit_WhenHookFails_StillPublishesInnerOnceAndDoesNotThrow()
    {
        var calls = new List<string>();
        var (sut, inner, _, uow) = CreateSut(
            calls,
            EventHookResult.Fail(new InvalidOperationException("hook failed")),
            hasAmbientUow: true);

        await sut.PublishAsync(new DurableEvent(), useOutbox: true);
        await uow.CommitAsync();

        calls.ShouldBe(["inner", "commit", "hook"]);
        await inner.Received(1).PublishAsync(
            Arg.Any<DurableEvent>(),
            Arg.Any<string?>(),
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DurablePostCommit_WithoutAmbientUow_PublishesInnerThenRunsHook()
    {
        var calls = new List<string>();
        var (sut, inner, _, _) = CreateSut(calls, EventHookResult.Ok(), hasAmbientUow: false);

        await sut.PublishAsync(new DurableEvent(), useOutbox: true);

        calls.ShouldBe(["inner", "hook"]);
        await inner.Received(1).PublishAsync(
            Arg.Any<DurableEvent>(),
            Arg.Any<string?>(),
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandledOrFallback_WhenHookSucceeds_DoesNotPublishInner()
    {
        var calls = new List<string>();
        var (sut, inner, _, _) = CreateSut(calls, EventHookResult.Ok(), hasAmbientUow: true);

        await sut.PublishAsync(new DefaultEvent(), useOutbox: true);

        calls.ShouldBe(["hook"]);
        await inner.DidNotReceive().PublishAsync(
            Arg.Any<DefaultEvent>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    private static (
        HookedDistributedEventBus Sut,
        IDistributedEventBus Inner,
        IEventHookInvoker Invoker,
        IUnitOfWork Uow) CreateSut(
        List<string> calls,
        EventHookResult hookResult,
        bool hasAmbientUow,
        ICorrelationIdProvider? correlationIdProvider = null)
    {
        var inner = Substitute.For<IDistributedEventBus>();
        inner.PublishAsync(
                Arg.Any<DurableEvent>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                calls.Add("inner");
                return Task.CompletedTask;
            });

        var durableInvoker = Substitute.For<IEventHookInvoker>();
        durableInvoker.EventType.Returns(typeof(DurableEvent));
        durableInvoker.HookName.Returns("durable-hook");
        durableInvoker.InvokeAsync(
                Arg.Any<object>(),
                Arg.Any<EventHookContext>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("hook");
                return Task.FromResult(hookResult);
            });

        var defaultInvoker = Substitute.For<IEventHookInvoker>();
        defaultInvoker.EventType.Returns(typeof(DefaultEvent));
        defaultInvoker.HookName.Returns("default-hook");
        defaultInvoker.InvokeAsync(
                Arg.Any<object>(),
                Arg.Any<EventHookContext>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("hook");
                return Task.FromResult(hookResult);
            });

        var services = new ServiceCollection();
        services.AddSingleton<IEventHookInvoker>(durableInvoker);
        services.AddSingleton<IEventHookInvoker>(defaultInvoker);
        var provider = services.BuildServiceProvider();

        var uow = Substitute.For<IUnitOfWork>();
        Func<IUnitOfWork, Task>? onCompleted = null;
        uow.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(callback => onCompleted = callback));
        uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            calls.Add("commit");
            if (onCompleted is not null)
                await onCompleted(uow);
        });

        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Current.Returns(hasAmbientUow ? uow : null);

        var sut = new HookedDistributedEventBus(
            inner,
            provider,
            uowManager,
            Substitute.For<ILogger<HookedDistributedEventBus>>(),
            correlationIdProvider);

        return (sut, inner, durableInvoker, uow);
    }

    [EventHook(EventHookMode.DurablePostCommit)]
    private sealed class DurableEvent;

    [EventHook]
    private sealed class DefaultEvent;

    private sealed class TraceableEvent : ITraceableDistributedEvent
    {
        public string? TraceParent { get; set; }
        public string? TraceState { get; set; }
        public string? RequestId { get; set; }
    }
}
