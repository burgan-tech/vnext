using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Infrastructure.EventBus;
using BBT.Workflow.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.EventBus;

/// <summary>
/// Pins the per-hook span emitted by <c>HookedDistributedEventBus.ExecuteHooksAsync</c>.
/// <para>
/// Before it existed, hooks ran inside <c>Uow.Commit</c> (DurablePostCommit) or
/// <c>Events.PublishDeferred</c> (HandledOrFallback) as one undifferentiated block: their remote
/// calls emitted client spans, but nothing attributed a call to a hook. The span lands under
/// whatever is ambient, so no re-parenting is involved.
/// </para>
/// </summary>
public sealed class HookedDistributedEventBusSpanTests : IDisposable
{
    private readonly List<Activity> _collected = new();
    private readonly ActivityListener _listener;

    public HookedDistributedEventBusSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "BBT.Workflow.Instances.Events",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _collected.Add
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    // The real EventHookMode enum has no "Immediate" member — its HandledOrFallback mode is what
    // runs at publish time (under Events.PublishDeferred), so that is what this probe declares.
    [EventHook(EventHookMode.HandledOrFallback)]
    private sealed class ProbeEvent;

    private static IEventHookInvoker StubInvoker(string hookName, EventHookResult result)
    {
        var invoker = Substitute.For<IEventHookInvoker>();
        invoker.EventType.Returns(typeof(ProbeEvent));
        invoker.HookName.Returns(hookName);
        invoker.InvokeAsync(Arg.Any<object>(), Arg.Any<EventHookContext>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return invoker;
    }

    private static IEventHookInvoker ThrowingInvoker(string hookName, Exception exception)
    {
        var invoker = Substitute.For<IEventHookInvoker>();
        invoker.EventType.Returns(typeof(ProbeEvent));
        invoker.HookName.Returns(hookName);
        // Explicit EventHookResult-returning helper (rather than an inline "_ => throw exception"
        // lambda) so overload resolution isn't ambiguous between NSubstitute's Returns<T> and its
        // Task<T>-unwrapping overload — both accept a bare throw expression.
        invoker.InvokeAsync(Arg.Any<object>(), Arg.Any<EventHookContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => Throw(exception));
        return invoker;
    }

    private static EventHookResult Throw(Exception exception) => throw exception;

    /// <summary>
    /// Builds a <see cref="HookedDistributedEventBus"/> wired to the real constructor, with the
    /// given invokers resolvable via the construction-time service provider (matches
    /// <c>GetInvokersForEventType</c>'s fallback path — <c>AmbientServiceProvider.Current</c> is
    /// null in this test process). The inner bus and UoW manager are never exercised for
    /// <see cref="EventHookMode.HandledOrFallback"/>, so bare substitutes are enough.
    /// </summary>
    private static HookedDistributedEventBus BuildBus(params IEventHookInvoker[] invokers)
    {
        var services = new ServiceCollection();
        foreach (var invoker in invokers)
        {
            services.AddSingleton(invoker);
        }
        var provider = services.BuildServiceProvider();

        var inner = Substitute.For<IDistributedEventBus>();
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Current.Returns((IUnitOfWork?)null);

        return new HookedDistributedEventBus(
            inner,
            provider,
            uowManager,
            NullLogger<HookedDistributedEventBus>.Instance);
    }

    [Fact]
    public async Task EachHook_GetsItsOwnNamedSpan_WithModeAndOutcome()
    {
        // Two hooks on one event → two spans, each named after ITS hook, both tagged with the mode.
        var first = StubInvoker("FirstEventHook", EventHookResult.Ok());
        var second = StubInvoker("SecondHook", EventHookResult.Ok());
        var bus = BuildBus(first, second);

        await bus.PublishAsync(new ProbeEvent(), useOutbox: true);

        var hookSpans = _collected.Where(a => a.DisplayName.StartsWith("EventHook.", StringComparison.Ordinal)).ToList();
        hookSpans.Count.ShouldBe(2);

        var byName = hookSpans.ToDictionary(a => a.DisplayName);
        byName.ShouldContainKey("EventHook.First");
        byName.ShouldContainKey("EventHook.Second");

        var firstSpan = byName["EventHook.First"];
        var secondSpan = byName["EventHook.Second"];

        firstSpan.GetTagItem(TelemetryConstants.TagNames.HookName).ShouldBe("FirstEventHook");
        secondSpan.GetTagItem(TelemetryConstants.TagNames.HookName).ShouldBe("SecondHook");

        foreach (var span in hookSpans)
        {
            span.GetTagItem(TelemetryConstants.TagNames.EventName).ShouldBe("ProbeEvent");
            span.GetTagItem(TelemetryConstants.TagNames.HookMode).ShouldBe(nameof(EventHookMode.HandledOrFallback));
            (span.Status is ActivityStatusCode.Unset or ActivityStatusCode.Ok).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task AFailedHook_ProducesAnErrorSpan_WithoutThrowing()
    {
        // One invoker returning a failed EventHookResult, and a second whose InvokeAsync throws.
        var failing = StubInvoker("FailingEventHook", EventHookResult.Fail(new InvalidOperationException("boom")));
        var throwing = ThrowingInvoker("ThrowingEventHook", new InvalidOperationException("kaboom"));
        var bus = BuildBus(failing, throwing);

        // Publish must not throw — hook failures stay swallowed — and both hooks must still run.
        await bus.PublishAsync(new ProbeEvent(), useOutbox: true);

        var hookSpans = _collected.Where(a => a.DisplayName.StartsWith("EventHook.", StringComparison.Ordinal)).ToList();
        hookSpans.Count.ShouldBe(2);

        var byName = hookSpans.ToDictionary(a => a.DisplayName);

        var failingSpan = byName["EventHook.Failing"];
        failingSpan.Status.ShouldBe(ActivityStatusCode.Error);
        failingSpan.StatusDescription.ShouldContain("boom");

        var throwingSpan = byName["EventHook.Throwing"];
        throwingSpan.Status.ShouldBe(ActivityStatusCode.Error);
        throwingSpan.StatusDescription.ShouldContain("kaboom");
    }

    [Fact]
    public async Task TheHookSpan_ParentsToTheAmbientActivity()
    {
        // Start an ambient activity named like the real enclosing span, publish, and assert the
        // hook span's ParentId equals that ambient activity's Id — this is the property that puts
        // hooks under whatever is ambient (Uow.Commit / Events.PublishDeferred) with no
        // re-parenting machinery.
        var ambient = new Activity("Events.PublishDeferred");
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.AddBaggage("probe.baggage", "value");
        ambient.Start();

        string? baggageSeenInsideHook = null;
        try
        {
            var invoker = Substitute.For<IEventHookInvoker>();
            invoker.EventType.Returns(typeof(ProbeEvent));
            invoker.HookName.Returns("ParentProbeEventHook");
            invoker.InvokeAsync(Arg.Any<object>(), Arg.Any<EventHookContext>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    // Captured DURING the hook call, while the hook span is Activity.Current.
                    // GetBaggageItem walks Activity.Parent, so this only sees the ambient baggage
                    // if the hook span was started with an IMPLICIT parent (Activity.Current) —
                    // starting it with an explicit parent context sets Parent to null and severs
                    // the chain even though ParentId still matches, which is exactly what the
                    // ParentId-only assertion below would fail to catch.
                    baggageSeenInsideHook = Activity.Current?.GetBaggageItem("probe.baggage");
                    return EventHookResult.Ok();
                });
            var bus = BuildBus(invoker);

            await bus.PublishAsync(new ProbeEvent(), useOutbox: true);
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }

        var hookSpan = _collected.Single(a => a.DisplayName.StartsWith("EventHook.", StringComparison.Ordinal));
        hookSpan.ParentId.ShouldBe(ambient.Id);
        baggageSeenInsideHook.ShouldBe("value");
    }
}
