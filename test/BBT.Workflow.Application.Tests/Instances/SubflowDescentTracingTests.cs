using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The descent span and the ambient depth that numbers it.
/// <para>
/// These cover the logic; the wiring into the five query descents, authorize and retry is covered by
/// the compiler and by the existing read-path tests. What is worth pinning here is the behaviour that
/// is easy to get subtly wrong and impossible to notice afterwards: depth restoration on the
/// exception path, the transport tag tracking the router's own predicate, and — the one that makes
/// the whole ladder work — <c>Activity.Current</c> being the descent span while the nested read runs.
/// </para>
/// </summary>
public sealed class SubflowDescentTracingTests : IDisposable
{
    private const string TargetDomain = "core";
    private const string TargetFlow = "chain-busy-middle";

    private readonly ActivityListener _listener;
    private readonly List<Activity> _captured = [];

    public SubflowDescentTracingTests()
    {
        _listener = new ActivityListener
        {
            // Matched against the const, never against InstanceReadActivityHelper.ActivitySource:
            // AddActivityListener runs this predicate while constructing sources, so touching the
            // helper's static field here re-enters its still-running initializer and poisons the type
            // for every later test in the process.
            ShouldListenTo = source => source.Name == InstanceReadActivityHelper.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => _captured.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        // A collector that is not listening turns every assertion below into a vacuous pass.
        if (!InstanceReadActivityHelper.ActivitySource.HasListeners())
            throw new InvalidOperationException("listener registered but the source has none");
    }

    public void Dispose() => _listener.Dispose();

    // ── span shape ───────────────────────────────────────────────────────────

    [Fact]
    public void TheSpan_CarriesTheTargetFlowInItsName_AndTheRestInTags()
    {
        using (InstanceReadActivityHelper.StartDescendScope(
                   Runtime(isLocal: true), TargetDomain, TargetFlow, "child-1", "parent-1",
                   TelemetryConstants.DescentFunctions.State))
        {
        }

        var span = _captured.ShouldHaveSingleItem();
        span.DisplayName.ShouldBe($"{InstanceReadActivityHelper.OperationDescend}/{TargetFlow}");
        Tag(span, TelemetryConstants.TagNames.DescentFunction).ShouldBe("state");
        Tag(span, TelemetryConstants.TagNames.InstanceId).ShouldBe("child-1");
        Tag(span, TelemetryConstants.TagNames.ParentInstanceId).ShouldBe("parent-1");
        Tag(span, TelemetryConstants.TagNames.SpanCategory)
            .ShouldBe(TelemetryConstants.SpanCategories.Business);
    }

    /// <summary>
    /// The transport tag must follow the router, not a second copy of its rule. Both branches are
    /// pinned because a tag that silently says "local" for a network hop is worse than no tag: it
    /// makes the expensive case look free.
    /// </summary>
    [Theory]
    [InlineData(true, "local")]
    [InlineData(false, "remote")]
    public void TheTransportTag_FollowsTheDomainMatchPredicate(bool isLocal, string expected)
    {
        using (InstanceReadActivityHelper.StartDescendScope(
                   Runtime(isLocal), TargetDomain, TargetFlow, "child-1", "parent-1",
                   TelemetryConstants.DescentFunctions.View))
        {
        }

        Tag(_captured.ShouldHaveSingleItem(), TelemetryConstants.TagNames.DescentTransport)
            .ShouldBe(expected);
    }

    [Fact]
    public void AFallback_IsMarkedSoItCannotPassForASuccessfulDescent()
    {
        using (var scope = StartLocal())
        {
            InstanceReadActivityHelper.SetUnresolved(scope.Activity, "subflow-view-unavailable");
        }

        Tag(_captured.ShouldHaveSingleItem(), TelemetryConstants.TagNames.DescentOutcome)
            .ShouldBe("subflow-view-unavailable");
    }

    // ── depth ────────────────────────────────────────────────────────────────

    [Fact]
    public void NestedDescents_AreNumberedByDepth()
    {
        using (StartLocal())
        {
            using (StartLocal())
            {
                using (StartLocal())
                {
                }
            }
        }

        // Captured on stop, so the innermost span closes first.
        _captured.Select(s => Tag(s, TelemetryConstants.TagNames.SubflowDepth)).ShouldBe([3, 2, 1]);
    }

    /// <summary>
    /// The reason the depth is a scope rather than a counter. A descent that throws must not leave the
    /// depth raised — the next sibling read would then number itself one level too deep, and nothing
    /// about the resulting trace would look wrong enough to investigate.
    /// </summary>
    [Fact]
    public void AThrowingDescent_RestoresTheDepthForItsSiblings()
    {
        SubflowDescentContext.Current.ShouldBe(0);

        Should.Throw<InvalidOperationException>(() =>
        {
            using (StartLocal())
            {
                throw new InvalidOperationException("descent blew up");
            }
        });

        SubflowDescentContext.Current.ShouldBe(0);

        using (StartLocal())
        {
            SubflowDescentContext.Current.ShouldBe(1);
        }
    }

    /// <summary>
    /// The depth flows across an <c>await</c> into the nested read — which is the whole reason it is
    /// an AsyncLocal and not a field.
    /// </summary>
    [Fact]
    public async Task TheDepth_FlowsIntoAwaitedWork()
    {
        using (StartLocal())
        {
            await Task.Yield();
            SubflowDescentContext.Current.ShouldBe(1);

            var observed = await Task.Run(() => SubflowDescentContext.Current);
            observed.ShouldBe(1);
        }

        SubflowDescentContext.Current.ShouldBe(0);
    }

    /// <summary>
    /// The cross-domain bridge. A hop that arrives carrying depth 2 must have its next descent
    /// numbered 3, not 1 — otherwise a mixed local/remote chain reports 1, 1, 2.
    /// </summary>
    [Fact]
    public void ASeededDepth_ContinuesTheLadderInsteadOfRestartingIt()
    {
        SubflowDescentContext.Seed(2);

        using (StartLocal())
        {
            Tag(_captured, expectedCount: 0);
        }

        Tag(_captured.ShouldHaveSingleItem(), TelemetryConstants.TagNames.SubflowDepth).ShouldBe(3);
    }

    /// <summary>
    /// An absent or malformed header must degrade to today's behaviour. Seeding a non-positive value
    /// leaves the ladder alone rather than producing a negative depth nobody would think to check for.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveSeed_IsIgnored(int seed)
    {
        SubflowDescentContext.Seed(seed);

        using (StartLocal())
        {
        }

        Tag(_captured.ShouldHaveSingleItem(), TelemetryConstants.TagNames.SubflowDepth).ShouldBe(1);
    }

    // ── parenting ────────────────────────────────────────────────────────────

    /// <summary>
    /// The property the whole design rests on: while the descent scope is open,
    /// <c>Activity.Current</c> IS the descent span. That is what makes the nested level's
    /// <c>Cache.*</c> and <c>Db.*</c> spans attach to their own level instead of piling up on the
    /// server span — and it is also why the read path's per-level tag stamping stopped colliding
    /// without any change to the stamping code itself.
    /// </summary>
    [Fact]
    public void WhileADescentIsOpen_ItIsTheCurrentActivity()
    {
        var outer = Activity.Current;

        using (var scope = StartLocal())
        {
            Activity.Current.ShouldBeSameAs(scope.Activity);
            Activity.Current.ShouldNotBeSameAs(outer);

            using (var inner = StartLocal())
            {
                Activity.Current.ShouldBeSameAs(inner.Activity);
                inner.Activity!.Parent.ShouldBeSameAs(scope.Activity);
            }
        }

        Activity.Current.ShouldBeSameAs(outer);
    }

    /// <summary>
    /// Baggage must survive the descent.
    /// <para>
    /// This is the test that catches the wrong <c>StartActivity</c> overload. Passing an explicit
    /// <c>ActivityContext</c> parent sets ParentSpanId but leaves <c>Activity.Parent</c> null, and
    /// baggage is inherited through the Activity CHAIN — so an explicitly-parented descent span cuts
    /// baggage off for everything under it. One level below this span, the cross-domain read calls
    /// <c>CurrentUserForwardHeadersHelper</c>, which forwards <c>X-Root-Instance-Id</c> by reading
    /// exactly this baggage back out. The failure would be silent and remote.
    /// </para>
    /// </summary>
    [Fact]
    public void ADescent_InheritsTheCallersBaggage()
    {
        using var caller = new Activity("caller").Start();
        caller.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, "root-1");

        using var scope = StartLocal();

        scope.Activity!.GetBaggageItem(TelemetryConstants.TagNames.RootInstanceId).ShouldBe("root-1");
        Activity.Current!.GetBaggageItem(TelemetryConstants.TagNames.RootInstanceId).ShouldBe("root-1");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IRuntimeInfoProvider Runtime(bool isLocal)
    {
        var provider = Substitute.For<IRuntimeInfoProvider>();
        provider.IsDomainMatch(Arg.Any<string>()).Returns(isLocal);
        return provider;
    }

    private static SubflowDescentScope StartLocal() =>
        InstanceReadActivityHelper.StartDescendScope(
            Runtime(isLocal: true), TargetDomain, TargetFlow, "child-1", "parent-1",
            TelemetryConstants.DescentFunctions.State);

    private static object? Tag(Activity activity, string name) => activity.GetTagItem(name);

    private static void Tag(List<Activity> captured, int expectedCount) =>
        captured.Count.ShouldBe(expectedCount);
}
