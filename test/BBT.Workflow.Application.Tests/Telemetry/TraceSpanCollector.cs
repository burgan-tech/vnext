using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Collects the stopped spans of ONE trace: the trace of a root activity this collector starts and
/// makes ambient, so every span the code under test opens beneath it is matched by
/// <see cref="Activity.TraceId"/>.
/// </summary>
/// <remarks>
/// An <see cref="ActivityListener"/> is process-wide: xUnit runs test classes in parallel, so the
/// listener also receives spans of other tests, on their threads. Counting everything it saw makes
/// <c>Assert.Single</c> flaky, and a plain <see cref="List{T}.Add"/> from <c>ActivityStopped</c> can
/// corrupt the list. This collector keeps only its own trace and adds under a lock.
/// Create it before the code under test runs, on the same flow (the root is set as
/// <see cref="Activity.Current"/>); dispose it at the end of the test.
/// </remarks>
public sealed class TraceSpanCollector : IDisposable
{
    private readonly List<Activity> _spans = new();
    private readonly ActivityListener _listener;
    private readonly Activity? _previous;

    public TraceSpanCollector(params string[] sourceNames)
        : this(source => sourceNames.Contains(source.Name))
    {
    }

    public TraceSpanCollector(Func<ActivitySource, bool> shouldListenTo)
    {
        _previous = Activity.Current;

        // A fresh trace, not a child of whatever happened to be ambient.
        Activity.Current = null;
        Root = new Activity("test-root").SetIdFormat(ActivityIdFormat.W3C).Start();
        TraceId = Root.TraceId;

        _listener = new ActivityListener
        {
            ShouldListenTo = shouldListenTo,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != TraceId)
                {
                    return;
                }

                lock (_spans)
                {
                    _spans.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>The ambient root every collected span descends from.</summary>
    public Activity Root { get; }

    public ActivityTraceId TraceId { get; }

    /// <summary>A snapshot of this trace's stopped spans, in stop order.</summary>
    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_spans)
            {
                return _spans.ToArray();
            }
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        Root.Stop();
        Activity.Current = _previous;
    }
}
