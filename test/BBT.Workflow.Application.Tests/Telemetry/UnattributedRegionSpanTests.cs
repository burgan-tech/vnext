using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the spans added for the stretches a transition used to spend outside every span.
/// <para>
/// Measured on trace <c>18e23262</c>: a tail transition of ~400 ms had ~140 ms inside
/// <c>SyncTransitionStrategy</c> that no child span covered, plus ~56 ms after it. The work was
/// real — realizing the continuation, settling the resting status, committing the transaction —
/// but a reader could only see that the hop took longer than its parts.
/// </para>
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class UnattributedRegionSpanTests : IDisposable
{
    private readonly List<ActivityListener> _listeners = new();

    public void Dispose()
    {
        foreach (var l in _listeners) l.Dispose();
        Activity.Current = null;
    }

    private void Listen(string sourceName, List<Activity> collected)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
    }

    [Fact]
    public async Task ContinuationDispatch_IsSpanned_AndNamesItsMode()
    {
        var collected = new List<Activity>();
        Listen("BBT.Workflow.Pipeline", collected);

        var strategy = Substitute.For<IContinuationStrategy>();
        strategy.Mode.Returns(ContinuationMode.Enqueue);
        strategy.DispatchAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowExecutionContext?>.Ok(null));
        var sut = new ContinuationDispatcher(new[] { strategy });

        await sut.DispatchAsync(ContinuationMode.Enqueue, null!, CancellationToken.None);

        var span = Assert.Single(collected, a => a.DisplayName.StartsWith("Transition.Continuation"));
        span.DisplayName.ShouldBe("Transition.Continuation/Enqueue");
        span.GetTagItem(TelemetryConstants.TagNames.ContinuationMode).ShouldBe("Enqueue");
        // Enqueue ends the in-process loop, so the tag records that no further hop followed.
        span.GetTagItem(TelemetryConstants.TagNames.ContinuationHasNext).ShouldBe(false);
    }

    [Fact]
    public async Task ContinuationDispatch_Failure_MarksTheSpanFailed()
    {
        var collected = new List<Activity>();
        Listen("BBT.Workflow.Pipeline", collected);

        var strategy = Substitute.For<IContinuationStrategy>();
        strategy.Mode.Returns(ContinuationMode.Inline);
        strategy.DispatchAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowExecutionContext?>.Fail(Error.Failure("Boom", "enqueue failed")));
        var sut = new ContinuationDispatcher(new[] { strategy });

        await sut.DispatchAsync(ContinuationMode.Inline, null!, CancellationToken.None);

        var span = Assert.Single(collected, a => a.DisplayName.StartsWith("Transition.Continuation"));
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public void GenerationTokenRead_IsSpanned()
    {
        // The token read precedes every component resolution and is a real round trip; the
        // caller's Cache.Get span sits after it, so before this it was attributed to nothing.
        var collected = new List<Activity>();
        Listen("BBT.Workflow.Cache", collected);

        using (CacheActivityHelper.StartActivity(
                   CacheActivityHelper.OperationGenerationGet, "gen:sys-flows:core:login-flow", "sys-flows"))
        {
        }

        var span = Assert.Single(collected);
        span.DisplayName.ShouldBe("Cache.GenerationGet/gen:sys-flows:core:login-flow");
        span.GetTagItem("cache.component_type").ShouldBe("sys-flows");
    }
}
