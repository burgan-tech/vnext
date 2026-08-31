using System;
using System.Diagnostics;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Logging;

/// <summary>
/// Pins the counter tag used to make cache HITS visible.
/// <para>
/// Two memos — the per-transition <c>ScriptContext</c> and the per-execution mapping-factory
/// dictionary — emit nothing at all when they hit, so a trace cannot distinguish "this work was
/// skipped" from "this work never happened". A span per hit would drown the tree (a 100-item
/// FanOut batch would add 100), so the enclosing span carries a count instead.
/// </para>
/// </summary>
public sealed class ActivityCounterExtensionsTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ActivitySource _source = new("Test.ActivityCounter");

    public ActivityCounterExtensionsTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Test.ActivityCounter",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    [Fact]
    public void FirstIncrement_SetsTheTagToOne()
    {
        using var activity = _source.StartActivity("probe")!;

        activity.IncrementCounterTag("vnext.test.hits");

        activity.GetTagItem("vnext.test.hits").ShouldBe(1);
    }

    [Fact]
    public void RepeatedIncrements_Accumulate()
    {
        using var activity = _source.StartActivity("probe")!;

        activity.IncrementCounterTag("vnext.test.hits");
        activity.IncrementCounterTag("vnext.test.hits");
        activity.IncrementCounterTag("vnext.test.hits");

        activity.GetTagItem("vnext.test.hits").ShouldBe(3);
    }

    [Fact]
    public void NullActivity_IsANoOp()
    {
        Activity? none = null;

        // Must not throw: every call site is on a hot path where no listener may be attached.
        Should.NotThrow(() => none.IncrementCounterTag("vnext.test.hits"));
    }

    [Fact]
    public void SeparateTags_CountIndependently()
    {
        using var activity = _source.StartActivity("probe")!;

        activity.IncrementCounterTag("vnext.test.a");
        activity.IncrementCounterTag("vnext.test.b");
        activity.IncrementCounterTag("vnext.test.a");

        activity.GetTagItem("vnext.test.a").ShouldBe(2);
        activity.GetTagItem("vnext.test.b").ShouldBe(1);
    }
}
